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
using KaleContentOps.Services;
using KaleContentOps.Services.CreativePerformance;
using KaleContentOps.Services.TikTok;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// Period interactions from the paginated video LIST endpoint
/// (GET /analytics/202605/shop_videos/performance, verified live 2026-10-22: top-level
/// per-video likes/comments/shares windowed by start_date_ge/end_date_lt).
/// Covered guarantees: complete pagination with dedupe (no double counting),
/// null != 0 (missing fields stay unavailable, explicit zeros are real),
/// scoped degradation (list failure never removes overview data), bounded
/// pagination guard (no partial totals), signature/window correctness per page.
/// </summary>
public class TikTokShopVideoOverviewInteractionsTests
{
    private const string OverviewPath = "/analytics/202609/shop_videos/overview_performance";
    private const string ListPath = "/analytics/202605/shop_videos/performance";

    /// <summary>Verified-shaped overview payload: one daily interval with video_views.</summary>
    private const string OverviewJson =
        """
        {
          "code": 0,
          "message": "Success",
          "request_id": "req-overview-0001",
          "data": {
            "latest_available_date": "2026-10-19",
            "performance": {
              "intervals": [
                { "start_date": "2026-10-14", "end_date": "2026-10-15", "video_views": 38771 },
                { "start_date": "2026-10-15", "end_date": "2026-10-16", "video_views": 31187 }
              ]
            }
          }
        }
        """;

    // ------------------------------------------------------------------
    // JSON builders (raw API token strings - string-encoded numbers included)
    // ------------------------------------------------------------------

    /// <summary>One data.videos[] entry; null fields are simply absent (never 0).</summary>
    private static string Video(string id, string? likes = null, string? comments = null, string? shares = null)
    {
        var fields = new List<string> { $"\"id\":\"{id}\"" };
        if (likes != null) fields.Add($"\"likes\":{likes}");
        if (comments != null) fields.Add($"\"comments\":{comments}");
        if (shares != null) fields.Add($"\"shares\":{shares}");
        return "{" + string.Join(",", fields) + "}";
    }

    private static string ListPage(string? nextToken, params string[] videos)
    {
        var next = nextToken is null ? "" : ",\"next_page_token\":\"" + nextToken + "\"";
        return "{\"code\":0,\"message\":\"Success\",\"request_id\":\"req-list\",\"data\":{\"videos\":["
            + string.Join(",", videos) + "]" + next + "}}";
    }

    private static HttpResponseMessage Ok(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ------------------------------------------------------------------
    // Harness (same wiring as TikTokShopVideoOverviewMetricSourceTests)
    // ------------------------------------------------------------------

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Task.FromResult(_respond(request));
        }
    }

    private sealed class StubAuth : ITikTokAuthService
    {
        public Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default)
            => Task.FromResult<TikTokTokenResponse?>(null);

        public Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult<TikTokTokenResponse?>(null);

        public Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default)
            => Task.FromResult<TikTokTokenResponse?>(null);

        public Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("TEST-ACCESS-TOKEN");
    }

    private static (TikTokShopVideoOverviewMetricSource Provider, RoutingHandler Handler, AppDbContext Db) CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        TikTokPeriodInteractionsCache? interactionsCache = null)
    {
        var handler = new RoutingHandler(respond);
        var factory = new SimpleHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
        });

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
        db.TikTokShops.Add(new TikTokShop { ShopCipher = "SHOP-CIPHER-1" });
        db.SaveChanges();

        var options = Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" });
        var provider = new TikTokShopVideoOverviewMetricSource(
            factory,
            options,
            db,
            new TikTokSignatureService(options),
            new StubAuth(),
            interactionsCache: interactionsCache);
        return (provider, handler, db);
    }

    private static AccountDailyMetricQuery Window()
        => new()
        {
            Start = new DateTime(2026, 10, 14),
            EndExclusive = new DateTime(2026, 10, 21)
        };

    private static bool IsOverview(HttpRequestMessage r) => r.RequestUri!.AbsolutePath == OverviewPath;

    private static bool IsList(HttpRequestMessage r) => r.RequestUri!.AbsolutePath == ListPath;

    // ------------------------------------------------------------------
    // Pagination + dedupe
    // ------------------------------------------------------------------

    [Fact]
    public async Task MultiPagePagination_SumsEveryVideoOnce_AndDeduplicatesReplayedIds()
    {
        // Page 2 replays video A (token replay / server quirk) plus new video C:
        // A's second appearance must NOT be summed again.
        var (provider, handler, db) = CreateProvider(request =>
        {
            if (IsOverview(request)) return Ok(OverviewJson);

            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            return query.TryGetValue("page_token", out var token) && token == "page-2"
                ? Ok(ListPage(null, Video("A", "10", "1", "2"), Video("C", "3", "2", "0")))
                : Ok(ListPage("page-2", Video("A", "10", "1", "2"), Video("B", "5", "0", "1")));
        });

        using (db)
        {
            var result = await provider.GetAsync(Window());

            Assert.True(result.IsAvailable);
            var dataset = result.Dataset!;

            var interactions = Assert.IsType<AccountPeriodInteractions>(dataset.PeriodInteractions);
            Assert.Equal(18, interactions.Likes); // 10 (A) + 5 (B) + 3 (C), replayed A not counted
            Assert.Equal(3, interactions.Comments);
            Assert.Equal(3, interactions.Shares);
            Assert.Equal(3, interactions.VideoCount); // unique ids only

            Assert.Contains(DailyReportMetrics.Interactions, dataset.AvailableMetricKeys!);

            // Overview data is untouched by the list flow.
            Assert.Equal(2, dataset.Records.Count);
            Assert.Equal(38771, dataset.Records[0].VideoViews);
            Assert.Contains(DailyReportMetrics.VideoViews, dataset.AvailableMetricKeys!);

            // Exactly two list calls; page 2 carries the server-provided token; both
            // pages use the SAME half-open window as the overview and are signed.
            var listRequests = handler.Requests.Where(IsList).ToList();
            Assert.Equal(2, listRequests.Count);
            foreach (var request in listRequests)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
                Assert.Equal("2026-10-14", query["start_date_ge"]);
                Assert.Equal("2026-10-21", query["end_date_lt"]);
                Assert.Equal("SHOP-CIPHER-1", query["shop_cipher"]);
                Assert.False(string.IsNullOrWhiteSpace(query["sign"]));
                Assert.True(request.Headers.Contains("x-tts-access-token"));
            }

            var secondQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(listRequests[1].RequestUri!.Query);
            Assert.Equal("page-2", secondQuery["page_token"]);

            // Video views must remain available (scoped degradation guarantee).
            Assert.True(dataset.AvailableMetricKeys!.Contains(TikTokShopVideoOverviewMetricSourceMetrics.VideoViews));
        }
    }

    // ------------------------------------------------------------------
    // Null != 0
    // ------------------------------------------------------------------

    [Fact]
    public async Task MissingInteractionFields_AreUnavailable_NeverZero()
    {
        // The videos exist but the schema never carries likes/comments/shares.
        var (provider, _, db) = CreateProvider(request => IsOverview(request)
            ? Ok(OverviewJson)
            : Ok(ListPage(null, Video("A"), Video("B"))));

        using (db)
        {
            var result = await provider.GetAsync(Window());

            Assert.True(result.IsAvailable); // overview still available
            Assert.Null(result.Dataset!.PeriodInteractions);
            Assert.DoesNotContain(DailyReportMetrics.Interactions, result.Dataset!.AvailableMetricKeys!);
        }
    }

    [Fact]
    public async Task ExplicitZeroFields_AreValidZeros_NotUnavailable()
    {
        var (provider, _, db) = CreateProvider(request => IsOverview(request)
            ? Ok(OverviewJson)
            : Ok(ListPage(null, Video("A", "0", "0", "0"))));

        using (db)
        {
            var result = await provider.GetAsync(Window());

            var interactions = Assert.IsType<AccountPeriodInteractions>(result.Dataset!.PeriodInteractions);
            Assert.Equal(0, interactions.Likes);
            Assert.Equal(0, interactions.Comments);
            Assert.Equal(0, interactions.Shares);
            Assert.Equal(1, interactions.VideoCount);
            Assert.Contains(DailyReportMetrics.Interactions, result.Dataset!.AvailableMetricKeys!);
        }
    }

    [Fact]
    public async Task StringEncodedNumbers_AreParsed()
    {
        var (provider, _, db) = CreateProvider(request => IsOverview(request)
            ? Ok(OverviewJson)
            : Ok(ListPage(null, Video("A", "\"10\"", "\"2\"", "\"3\""))));

        using (db)
        {
            var result = await provider.GetAsync(Window());

            var interactions = Assert.IsType<AccountPeriodInteractions>(result.Dataset!.PeriodInteractions);
            Assert.Equal(10, interactions.Likes);
            Assert.Equal(2, interactions.Comments);
            Assert.Equal(3, interactions.Shares);
        }
    }

    [Fact]
    public async Task EmptyVideosArray_IsARealZeroWindow()
    {
        // A verified empty videos array means the window has no videos at all:
        // 0 (from 0 videos) is a complete, honest total.
        var (provider, _, db) = CreateProvider(request => IsOverview(request)
            ? Ok(OverviewJson)
            : Ok(ListPage(null)));

        using (db)
        {
            var result = await provider.GetAsync(Window());

            var interactions = Assert.IsType<AccountPeriodInteractions>(result.Dataset!.PeriodInteractions);
            Assert.Equal(0, interactions.Likes);
            Assert.Equal(0, interactions.Comments);
            Assert.Equal(0, interactions.Shares);
            Assert.Equal(0, interactions.VideoCount);
            Assert.Contains(DailyReportMetrics.Interactions, result.Dataset!.AvailableMetricKeys!);
        }
    }

    [Fact]
    public async Task MissingVideosArray_IsSchemaMismatch_NotZeros()
    {
        // code=0 without the verified videos array is NOT an empty window - it must
        // never be presented as a zero period total.
        var (provider, _, db) = CreateProvider(request => IsOverview(request)
            ? Ok(OverviewJson)
            : Ok("{\"code\":0,\"message\":\"Success\",\"request_id\":\"req-list\",\"data\":{\"latest_available_date\":\"2026-10-19\"}}"));

        using (db)
        {
            var result = await provider.GetAsync(Window());

            Assert.True(result.IsAvailable);
            Assert.Null(result.Dataset!.PeriodInteractions);
            Assert.DoesNotContain(DailyReportMetrics.Interactions, result.Dataset!.AvailableMetricKeys!);
            // Overview data survives the list schema mismatch.
            Assert.Equal(2, result.Dataset!.Records.Count);
        }
    }

    // ------------------------------------------------------------------
    // Errors and partial results
    // ------------------------------------------------------------------

    [Fact]
    public async Task HttpErrorOnSecondPage_ReportsUnavailable_NeverPartialTotals()
    {
        var (provider, _, db) = CreateProvider(request =>
        {
            if (IsOverview(request)) return Ok(OverviewJson);

            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (query.TryGetValue("page_token", out var token) && token == "page-2")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"code\":500,\"message\":\"boom\"}", Encoding.UTF8, "application/json")
                };
            }

            return Ok(ListPage("page-2", Video("A", "10", "1", "2")));
        });

        using (db)
        {
            var result = await provider.GetAsync(Window());

            Assert.True(result.IsAvailable); // overview intact
            Assert.Null(result.Dataset!.PeriodInteractions); // partial sum is never returned
            Assert.DoesNotContain(DailyReportMetrics.Interactions, result.Dataset!.AvailableMetricKeys!);
        }
    }

    [Fact]
    public async Task BusinessError_ReportsUnavailable()
    {
        var (provider, _, db) = CreateProvider(request => IsOverview(request)
            ? Ok(OverviewJson)
            : Ok("{\"code\":36009002,\"message\":\"rate limit exceeded\",\"request_id\":\"req-err\"}"));

        using (db)
        {
            var result = await provider.GetAsync(Window());

            Assert.True(result.IsAvailable);
            Assert.Null(result.Dataset!.PeriodInteractions);
            Assert.DoesNotContain(DailyReportMetrics.Interactions, result.Dataset!.AvailableMetricKeys!);
        }
    }

    [Fact]
    public async Task MalformedListJson_KeepsOverviewAvailable()
    {
        var (provider, _, db) = CreateProvider(request => IsOverview(request)
            ? Ok(OverviewJson)
            : Ok("not-json"));

        using (db)
        {
            var result = await provider.GetAsync(Window());

            Assert.True(result.IsAvailable);
            Assert.Null(result.Dataset!.PeriodInteractions);
            Assert.Equal(38771 + 31187, result.Dataset!.Records.Sum(r => r.VideoViews!.Value));
        }
    }

    [Fact]
    public async Task ListHttpError_KeepsOverviewAvailable()
    {
        var (provider, _, db) = CreateProvider(request => IsOverview(request)
            ? Ok(OverviewJson)
            : new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream error", Encoding.UTF8, "text/plain")
            });

        using (db)
        {
            var result = await provider.GetAsync(Window());

            Assert.True(result.IsAvailable);
            Assert.Null(result.Dataset!.PeriodInteractions);
            Assert.DoesNotContain(DailyReportMetrics.Interactions, result.Dataset!.AvailableMetricKeys!);
            Assert.Contains(DailyReportMetrics.VideoViews, result.Dataset!.AvailableMetricKeys!);
        }
    }

    [Fact]
    public async Task RateLimitedOnceOnList_IsRetriedAfterRetryAfter_ThenSucceeds()
    {
        var listCalls = 0;
        var (provider, _, db) = CreateProvider(request =>
        {
            if (IsOverview(request)) return Ok(OverviewJson);

            listCalls++;
            if (listCalls == 1)
            {
                var limited = new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{\"code\":36009002,\"message\":\"rate limit\"}", Encoding.UTF8, "application/json")
                };
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return limited;
            }

            return Ok(ListPage(null, Video("A", "7", "1", "1")));
        });

        using (db)
        {
            var result = await provider.GetAsync(Window());

            Assert.True(result.IsAvailable);
            var interactions = Assert.IsType<AccountPeriodInteractions>(result.Dataset!.PeriodInteractions);
            Assert.Equal(7, interactions.Likes);
            Assert.Equal(2, listCalls); // one bounded retry, not a hammering loop
        }
    }

    [Fact]
    public async Task PaginationGuard_NeverReturnsPartialTotals()
    {
        // The server keeps handing out a next token forever: the bounded guard must
        // report unavailable instead of presenting the partial sum as the period total.
        var (provider, handler, db) = CreateProvider(request =>
        {
            if (IsOverview(request)) return Ok(OverviewJson);

            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            var token = query.TryGetValue("page_token", out var t) ? t.ToString() : "0";
            var page = int.TryParse(token, out var n) ? n : 0;
            return Ok(ListPage("page-" + (page + 1), Video("V" + page, "1", "1", "1")));
        });

        using (db)
        {
            var result = await provider.GetAsync(Window());

            Assert.True(result.IsAvailable);
            Assert.Null(result.Dataset!.PeriodInteractions);
            Assert.DoesNotContain(DailyReportMetrics.Interactions, result.Dataset!.AvailableMetricKeys!);
            // Exactly ListMaxPages list requests were made - the guard is bounded.
            Assert.Equal(TikTokShopVideoOverviewMetricSource.ListMaxPages, handler.Requests.Count(IsList));
        }
    }

    // ------------------------------------------------------------------
    // Rate-limit cost control: TTL cache of period interaction outcomes
    // ------------------------------------------------------------------

    [Fact]
    public async Task SecondRenderWithinTtl_ServesInteractionsFromCache_WithoutNewListRequests()
    {
        // The list endpoint returns the WHOLE catalog per window (~28 pages live):
        // a repeat render inside the TTL must not re-walk the pagination.
        var cache = new TikTokPeriodInteractionsCache();
        var (provider, handler, db) = CreateProvider(
            request => IsOverview(request)
                ? Ok(OverviewJson)
                : Ok(ListPage(null, Video("A", "10", "1", "2"))),
            cache);

        using (db)
        {
            var first = await provider.GetAsync(Window());
            var listCallsAfterFirst = handler.Requests.Count(IsList);
            Assert.True(listCallsAfterFirst > 0);

            var second = await provider.GetAsync(Window());

            // Same totals, zero additional TikTok list requests.
            Assert.Equal(listCallsAfterFirst, handler.Requests.Count(IsList));
            Assert.Equal(10, second.Dataset!.PeriodInteractions!.Likes);
            Assert.Equal(first.Dataset!.PeriodInteractions!.Likes, second.Dataset!.PeriodInteractions!.Likes);
        }
    }

    [Fact]
    public async Task CacheIsKeyedByWindow_AnotherWindowFetchesFreshTotals()
    {
        var cache = new TikTokPeriodInteractionsCache();
        var (provider, handler, db) = CreateProvider(
            request => IsOverview(request)
                ? Ok(OverviewJson)
                : Ok(ListPage(null, Video("A", "10", "1", "2"))),
            cache);

        using (db)
        {
            await provider.GetAsync(Window());
            var afterFirst = handler.Requests.Count(IsList);

            // A DIFFERENT window must never reuse the cached total of the first one.
            await provider.GetAsync(new AccountDailyMetricQuery
            {
                Start = new DateTime(2026, 10, 10),
                EndExclusive = new DateTime(2026, 10, 14)
            });

            Assert.True(handler.Requests.Count(IsList) > afterFirst);
        }
    }

    [Fact]
    public async Task FailedListOutcome_IsAlsoCached_SoTheApiIsNotHammered()
    {        // A rate-limited/erroring list endpoint must not be re-walked on every render.
        var cache = new TikTokPeriodInteractionsCache();
        var (provider, handler, db) = CreateProvider(
            request => IsOverview(request)
                ? Ok(OverviewJson)
                : new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("upstream error", Encoding.UTF8, "text/plain")
                },
            cache);

        using (db)
        {
            var first = await provider.GetAsync(Window());
            var listCallsAfterFirst = handler.Requests.Count(IsList);
            Assert.Null(first.Dataset!.PeriodInteractions);

            var second = await provider.GetAsync(Window());

            Assert.Null(second.Dataset!.PeriodInteractions); // still honestly unavailable
            Assert.Equal(listCallsAfterFirst, handler.Requests.Count(IsList)); // no new attempts
        }
    }
}
