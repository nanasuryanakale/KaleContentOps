using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KaleContentOps.Tests.TikTok.BatchSync;

/// <summary>
/// Shared harness for batch-sync tests. ALL HTTP goes through this fake handler -
/// automated tests never call the real TikTok API.
/// </summary>
internal sealed class FakeTikTokApiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"message\":\"OK\",\"data\":{\"videos\":[]}}", Encoding.UTF8, "application/json") };

    /// <summary>Ordered log of request kinds: "list?page_token=X" or "details:VIDEO_ID".</summary>
    public List<string> RequestLog { get; } = new();

    /// <summary>Query params of every list request, in order (key -> raw value).</summary>
    public List<Dictionary<string, string>> ListRequestQueries { get; } = new();

    /// <summary>Query params of every per-video details request, in order (key -> raw value).</summary>
    public List<Dictionary<string, string>> DetailsRequestQueries { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = QueryHelpers.ParseQuery(request.RequestUri!.Query)
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);

        if (request.RequestUri.AbsolutePath.Contains("/shop_videos/performance", StringComparison.Ordinal))
        {
            RequestLog.Add("list:" + (query.TryGetValue("page_token", out var t) ? t : ""));
            ListRequestQueries.Add(query);
        }
        else
        {
            var seg = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var idx = Array.IndexOf(seg, "shop_videos");
            var videoId = idx >= 0 && idx + 1 < seg.Length ? seg[idx + 1] : "?";
            RequestLog.Add("details:" + videoId);
            DetailsRequestQueries.Add(query);
        }

        return Task.FromResult(Responder(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter != null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }
}

internal sealed class StubAuth : ITikTokAuthService
{
    public Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
    public Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
    public Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
    public Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("TEST-TOKEN");
}

/// <summary>Builds a fully wired (but HTTP-faked) batch sync stack over one InMemory database.</summary>
internal sealed class BatchSyncTestContext : IDisposable
{
    public AppDbContext Db { get; }
    public FakeTikTokApiHandler Handler { get; }
    public TikTokBatchSyncService Service { get; }
    public TikTokDetailsSyncService DetailsService { get; }
    public ITikTokVideoService VideoService { get; }
    public TikTokOptions Options { get; }
    public string DatabaseName { get; }

    public BatchSyncTestContext(TikTokOptions? options = null)
    {
        DatabaseName = "BatchSync_" + Guid.NewGuid().ToString("N");
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(DatabaseName)
            .Options;
        Db = new AppDbContext(dbOptions);

        Db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
        Db.SaveChanges();

        Handler = new FakeTikTokApiHandler();
        var http = new SimpleHttpClientFactory(new HttpClient(Handler)
        {
            BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
        });

        Options = options ?? new TikTokOptions
        {
            AppKey = "K",
            AppSecret = "S",
            BatchSyncRetryBaseSeconds = 0,       // no real waiting in tests
            BatchSyncMaxAttempts = 3,
            BatchSyncMaxItemAttempts = 3,
            BatchSyncMaxRateLimitPauses = 2,
            BatchSyncLeaseMinutes = 10,
            BatchSyncMaxPages = 50,
            BatchSyncMaxRangeDays = 366
        };

        var optionsProvider = Microsoft.Extensions.Options.Options.Create(Options);
        var signature = new TikTokSignatureService(optionsProvider);
        VideoService = new TikTokVideoService(http, optionsProvider, Db, new StubAuth(), signature);
        DetailsService = new TikTokDetailsSyncService(http, optionsProvider, Db, VideoService);
        Service = new TikTokBatchSyncService(Db, VideoService, DetailsService, logger: null, options: optionsProvider);
    }

    /// <summary>A second, independent DbContext over the SAME database (concurrency tests).</summary>
    public AppDbContext CreateSecondContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(DatabaseName).Options);

    /// <summary>A second, independent service instance over the SAME database (second worker).</summary>
    public TikTokBatchSyncService CreateSecondWorker()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(DatabaseName)
            .Options;
        var db2 = new AppDbContext(dbOptions);
        var http = new SimpleHttpClientFactory(new HttpClient(Handler)
        {
            BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
        });
        var optionsProvider = Microsoft.Extensions.Options.Options.Create(Options);
        var video = new TikTokVideoService(http, optionsProvider, db2, new StubAuth(), new TikTokSignatureService(optionsProvider));
        var details = new TikTokDetailsSyncService(http, optionsProvider, db2, video);
        return new TikTokBatchSyncService(db2, video, details, null, optionsProvider);
    }

    public async Task<TikTokShop> AddShopAsync(string cipher = "SHOP-TEST")
    {
        var shop = new TikTokShop { ShopCipher = cipher, ShopName = "Test Shop", ShopId = "S1", ShopCode = "SC" };
        Db.TikTokShops.Add(shop);
        await Db.SaveChangesAsync();
        return shop;
    }

    private static readonly DateTime RangeStart = new(2026, 9, 1);
    private static readonly DateTime RangeEnd = new(2026, 9, 7);

    /// <summary>Job that has finished collecting video ids (details phase not started).</summary>
    public async Task<TikTokBatchSyncJob> SeedCollectedJobAsync(long shopId, params string[] videoIds)
    {
        var job = new TikTokBatchSyncJob
        {
            TikTokShopId = shopId,
            StartDate = RangeStart,
            EndDate = RangeEnd,
            Status = TikTokBatchSyncJobStatus.Queued,
            CollectionCompleted = true,
            CreatedAt = DateTime.UtcNow
        };
        Db.TikTokBatchSyncJobs.Add(job);
        await Db.SaveChangesAsync();

        foreach (var videoId in videoIds)
        {
            Db.TikTokBatchSyncJobItems.Add(new TikTokBatchSyncJobItem
            {
                TikTokBatchSyncJobId = job.Id,
                VideoId = videoId,
                Status = TikTokBatchSyncJobItemStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
        }
        await Db.SaveChangesAsync();
        return job;
    }

    /// <summary>Active job whose row lease has expired (simulates a crashed worker).</summary>
    public async Task<TikTokBatchSyncJob> SeedStaleActiveJobAsync(
        long shopId,
        TikTokBatchSyncJobStatus status,
        bool collectionCompleted,
        string? currentPageToken,
        params (string VideoId, TikTokBatchSyncJobItemStatus Status, int Attempts)[] items)
    {
        var job = new TikTokBatchSyncJob
        {
            TikTokShopId = shopId,
            StartDate = RangeStart,
            EndDate = RangeEnd,
            Status = status,
            CollectionCompleted = collectionCompleted,
            CurrentPageToken = currentPageToken,
            StartedAt = DateTime.UtcNow.AddHours(-2),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-30), // lease (10 min) expired
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        };
        Db.TikTokBatchSyncJobs.Add(job);
        await Db.SaveChangesAsync();

        foreach (var (videoId, itemStatus, attempts) in items)
        {
            Db.TikTokBatchSyncJobItems.Add(new TikTokBatchSyncJobItem
            {
                TikTokBatchSyncJobId = job.Id,
                VideoId = videoId,
                Status = itemStatus,
                AttemptCount = attempts,
                CreatedAt = DateTime.UtcNow.AddHours(-2)
            });
        }
        await Db.SaveChangesAsync();
        return job;
    }

    public static string ListPageJson(string? nextPageToken, params string[] videoIds)
    {
        var videos = string.Join(",", videoIds.Select(id =>
            $"{{\"id\":\"{id}\",\"title\":\"Title {id}\",\"username\":\"creator{id}\",\"video_post_time\":\"2026-09-05T10:00:00\",\"views\":100}}"));
        var tokenPart = nextPageToken == null ? "" : $"\"page_token\":\"{nextPageToken}\",";
        return $"{{\"code\":0,\"message\":\"OK\",\"data\":{{{tokenPart}\"videos\":[{videos}]}}}}";
    }

    public static string EmptyListJson() => "{\"code\":0,\"message\":\"OK\",\"data\":{\"videos\":[]}}";

    /// <summary>Real details wire shape (verified fixture style): engagement only, no demographics.</summary>
    public static string DetailsJson(long views = 263, int likes = 13, bool withViewersProfile = false)
    {
        var demographics = withViewersProfile
            ? "\"viewer_profile\":[{\"type\":\"VIEWERS\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.600\"},{\"gender\":\"female\",\"percentage\":\"0.400\"}],\"age_distribution\":[{\"age\":\"18-24\",\"percentage\":\"0.500\"}],\"country_distribution\":[{\"country_code\":\"ID\",\"percentage\":\"0.990\"}]}]"
            : "\"viewer_profile\":[]";
        return $$"""
            {
              "code": 0,
              "message": "OK",
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": { "comments": 2, "likes": {{likes}}, "new_followers": 1, "shares": 3, "views": {{views}} },
                      "sales": { "overall": { "ctr": "0.0570", "customers": 0, "gmv": { "amount": "0.00", "currency": "IDR" }, "items_sold": 0 } }
                    }
                  ],
                  {{demographics}}
                }
              }
            }
            """;
    }

    public void Dispose() => Db.Dispose();
}
