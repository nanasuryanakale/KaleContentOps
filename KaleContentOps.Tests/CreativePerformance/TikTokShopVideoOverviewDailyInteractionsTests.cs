using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
/// DAILY interactions via the 202609 batch endpoint (verified live 2026-10-22:
/// granularity=1D returns per-video daily intervals whose traffic object carries
/// likes/comments/shares; window span at most 7 days). Covered guarantees:
///  - per-day aggregation with video-id dedupe (replayed ids never double count),
///  - null != 0 (explicit zeros are real, absent traffic never becomes a zero total),
///  - ALL-OR-NOTHING: any chunk failure / business error / lookback rejection /
///    schema mismatch / catalog-coverage gap falls back to the list-based period
///    totals - a partial daily sum is never presented as the period total,
///  - <=7-day sub-window splitting for longer windows, bounded by a request budget,
///  - cache: a second render inside the TTL makes zero TikTok requests.
/// </summary>
public class TikTokShopVideoOverviewDailyInteractionsTests
{
    private const string OverviewPath = "/analytics/202609/shop_videos/overview_performance";
    private const string ListPath = "/analytics/202605/shop_videos/performance";

    private static readonly DateTime Today = new(2026, 10, 22);

    /// <summary>Verified-shaped overview payload covering the requested window daily.</summary>
    private static string OverviewJson(DateTime start, DateTime endExclusive)
    {
        var intervals = new List<string>();
        for (var day = start; day < endExclusive; day = day.AddDays(1))
        {
            intervals.Add($"{{\"start_date\":\"{day:yyyy-MM-dd}\",\"end_date\":\"{day.AddDays(1):yyyy-MM-dd}\",\"video_views\":1000}}");
        }

        return "{\"code\":0,\"message\":\"Success\",\"request_id\":\"req-overview\",\"data\":{\"latest_available_date\":\""
            + Today.AddDays(-1).ToString("yyyy-MM-dd")
            + "\",\"performance\":{\"intervals\":[" + string.Join(",", intervals) + "]}}}";
    }

    /// <summary>One batch data.videos[] entry (verified shape: video_id + performance.intervals[]).</summary>
    private static string BatchEntry(string id, params string[] intervals)
        => "{\"video_id\":\"" + id + "\",\"performance\":{\"intervals\":[" + string.Join(",", intervals) + "]}}";

    /// <summary>One daily interval. A null field is ABSENT from the payload (never 0).</summary>
    private static string DailyInterval(DateTime date, long? likes = null, long? comments = null, long? shares = null)
    {
        var traffic = new List<string>();
        if (likes.HasValue) traffic.Add($"\"likes\":{likes.Value}");
        if (comments.HasValue) traffic.Add($"\"comments\":{comments.Value}");
        if (shares.HasValue) traffic.Add($"\"shares\":{shares.Value}");
        traffic.Add("\"views\":10");
        return "{\"start_date\":\"" + date.ToString("yyyy-MM-dd") + "\",\"end_date\":\""
            + date.AddDays(1).ToString("yyyy-MM-dd") + "\",\"traffic\":{" + string.Join(",", traffic) + "}}";
    }

    private static string MultiDayInterval(DateTime start, DateTime endExclusive)
        => "{\"start_date\":\"" + start.ToString("yyyy-MM-dd") + "\",\"end_date\":\""
            + endExclusive.ToString("yyyy-MM-dd") + "\",\"traffic\":{\"likes\":7,\"comments\":7,\"shares\":7,\"views\":10}}";

    private static HttpResponseMessage Ok(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>List page (verified shape) with optional next token and optional total.</summary>
    private static string ListPage(string? nextToken, string? totalCount, params string[] videos)
    {
        var next = nextToken is null ? "" : ",\"next_page_token\":\"" + nextToken + "\"";
        var total = totalCount is null ? "" : ",\"total_count\":" + totalCount;
        return "{\"code\":0,\"message\":\"Success\",\"request_id\":\"req-list\",\"data\":{\"videos\":["
            + string.Join(",", videos) + "]" + next + total + "}}";
    }

    private static string ListVideo(string id, long likes, long comments, long shares)
        => "{\"id\":\"" + id + "\",\"likes\":" + likes + ",\"comments\":" + comments + ",\"shares\":" + shares + "}";

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        public int ListRequestCount
        {
            get
            {
                lock (Requests)
                {
                    return Requests.Count(r => r.RequestUri!.AbsolutePath == ListPath);
                }
            }
        }

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

    private sealed class FakeVideoService : ITikTokVideoService
    {
        public List<(IReadOnlyList<string> Ids, DateTime Start, DateTime End, string? Granularity)> Calls { get; } = new();

        public Func<IReadOnlyList<string>, DateTime, DateTime, TikTokBatchPerformanceResult>? Responder { get; set; }

        public Task<TikTokBatchPerformanceResult> FetchBatchPerformanceAsync(
            IReadOnlyList<string> videoIds,
            string shopCipher,
            DateTime startDateInclusive,
            DateTime endDateExclusive,
            string? granularity,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((videoIds.ToList(), startDateInclusive, endDateExclusive, granularity));
            var result = Responder?.Invoke(videoIds, startDateInclusive, endDateExclusive)
                ?? new TikTokBatchPerformanceResult(200, 0, "Success", "req-batch", Array.Empty<JsonElement>());
            return Task.FromResult(result);
        }

        public Task<int> FetchAndSaveVideoListAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TikTokVideoListPage> FetchVideoListPageAsync(string shopCipher, string? startDateIso, string? endDateIso, string? pageToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> ApplyVideoListPageAsync(string shopCipher, TikTokVideoListPage page, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DryRunReport> DryRunVideoClassificationAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(int? StatusCode, string Endpoint, JsonElement? Root, JsonElement? Matched)> RunPerformanceDiagnosticAsync(string videoId, string shopCipher, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(int? StatusCode, string Endpoint, JsonElement? Root, JsonElement? Data, int? RetryAfterSeconds)> RunDetailsDiagnosticAsync(
            string videoId, string shopCipher, CancellationToken cancellationToken = default, DateTime? startDateInclusive = null, DateTime? endDateExclusive = null)
            => throw new NotSupportedException();
    }

    private static TikTokBatchPerformanceResult BatchOk(params string[] entries)
    {
        using var doc = JsonDocument.Parse(
            "{\"code\":0,\"message\":\"Success\",\"request_id\":\"req-batch\",\"data\":{\"videos\":[" + string.Join(",", entries) + "]}}");
        return new TikTokBatchPerformanceResult(200, 0, "Success", "req-batch",
            doc.RootElement.GetProperty("data").GetProperty("videos").EnumerateArray().Select(e => e.Clone()).ToList());
    }

    private static (TikTokShopVideoOverviewMetricSource Provider, RoutingHandler Handler, FakeVideoService VideoService, AppDbContext Db) CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        TikTokPeriodInteractionsCache? cache = null)
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
        var videoService = new FakeVideoService();
        var provider = new TikTokShopVideoOverviewMetricSource(
            factory,
            options,
            db,
            new TikTokSignatureService(options),
            new StubAuth(),
            interactionsCache: cache,
            videoService: videoService);
        return (provider, handler, videoService, db);
    }

    private static async Task SeedVideosAsync(AppDbContext db, params (string Id, DateTime? Post)[] videos)
    {
        var shop = await db.TikTokShops.FirstAsync();
        foreach (var (id, post) in videos)
        {
            db.ContentLogs.Add(new ContentLog
            {
                VideoId = id,
                TikTokShopId = shop.Id,
                ProductionMethodId = 1,
                VideoPostTime = post
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Standard responder: overview + list page WITHOUT a catalog total.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> OverviewAndList(Func<HttpResponseMessage?>? list = null)
        => request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return Ok(OverviewJson(
                    DateTime.Parse(query["start_date_ge"]!),
                    DateTime.Parse(query["end_date_lt"]!)));
            }

            if (path == ListPath && list is not null)
            {
                return list()!;
            }

            return Ok("{\"code\":0,\"data\":{\"videos\":[]}}");
        };

    private static AccountDailyMetricQuery Window(int startMonth, int startDay, int endMonth, int endDay)
        => new()
        {
            Start = new DateTime(2026, startMonth, startDay),
            EndExclusive = new DateTime(2026, endMonth, endDay)
        };

    // ------------------------------------------------------------------
    // Success path: per-day aggregation + dedupe + period totals
    // ------------------------------------------------------------------

    [Fact]
    public async Task BatchDaily_SumsPerDay_DedupesReplayedIds_AndSetsPeriodTotals()
    {
        var window = Window(10, 19, 10, 22); // 3 days
        var (provider, handler, videoService, db) = CreateProvider(OverviewAndList());
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 10, 10)), ("V-2", new DateTime(2026, 10, 20)));

        var d19 = new DateTime(2026, 10, 19);
        var d20 = new DateTime(2026, 10, 20);
        var d21 = new DateTime(2026, 10, 21);
        videoService.Responder = (_, _, _) => BatchOk(
            BatchEntry("V-1", DailyInterval(d19, likes: 3, comments: 1, shares: 0)),
            // replayed id with different values: MUST be ignored (dedupe by id)
            BatchEntry("V-1", DailyInterval(d19, likes: 999, comments: 999, shares: 999)),
            BatchEntry("V-2", DailyInterval(d20, likes: 2, comments: 2, shares: 2)),
            // unknown id returned by the API: still counted once
            BatchEntry("V-X", DailyInterval(d21, likes: 5)),
            BatchEntry("V-3", DailyInterval(d21)));

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        var records = result.Dataset!.Records.ToDictionary(r => r.Date);

        // Day with explicit 0 shares: 0 is a real, counted value.
        Assert.Equal(3, records[d19].Likes);
        Assert.Equal(1, records[d19].Comments);
        Assert.Equal(0, records[d19].Shares);
        Assert.Equal(2, records[d20].Likes);
        Assert.Equal(5, records[d21].Likes); // only V-X (5); V-3 has NO likes field -> contributes nothing

        // Day WITHOUT any interval stays null - never a fabricated zero.
        Assert.Null(records[d21].Comments);
        Assert.Equal(2, records[d20].Shares);

        var totals = result.Dataset.PeriodInteractions;
        Assert.NotNull(totals);
        Assert.Equal(3 + 2 + 5, totals!.Likes); // replayed 999 never added
        Assert.Equal(1 + 2, totals.Comments);
        Assert.Equal(0 + 2, totals.Shares);
        Assert.Equal(4, totals.VideoCount); // V-1, V-2, V-X, V-3

        Assert.Contains(DailyReportMetrics.Interactions, result.Dataset.AvailableMetricKeys!);

        var call = Assert.Single(videoService.Calls);
        Assert.Equal("1D", call.Granularity);
        Assert.Equal(window.Start, call.Start);
        Assert.Equal(window.EndExclusive, call.End);
    }

    [Fact]
    public async Task BatchDaily_MultiDayInterval_IsSkipped_NeverSplit()
    {
        var window = Window(10, 19, 10, 22);
        var (provider, _, videoService, db) = CreateProvider(OverviewAndList());
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 10, 10)));

        var d19 = new DateTime(2026, 10, 19);
        videoService.Responder = (_, _, _) => BatchOk(
            BatchEntry("V-1", MultiDayInterval(d19, new DateTime(2026, 10, 21)), DailyInterval(d19, likes: 4)));

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        var totals = result.Dataset!.PeriodInteractions!;
        Assert.Equal(4, totals.Likes); // multi-day interval contributed NOTHING
    }

    [Fact]
    public async Task BatchDaily_EmptyLocalCatalogWithMatchingApiTotal_IsRealZero()
    {
        var window = Window(10, 19, 10, 22);
        var (provider, handler, videoService, db) = CreateProvider(OverviewAndList(
            () => Ok(ListPage(null, "0"))));

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        var totals = result.Dataset!.PeriodInteractions!;
        Assert.Equal(0, totals.Likes);
        Assert.Equal(0, totals.Comments);
        Assert.Equal(0, totals.Shares);
        Assert.Equal(0, totals.VideoCount);
        Assert.Empty(videoService.Calls); // no ids -> no batch requests
        Assert.Contains(DailyReportMetrics.Interactions, result.Dataset.AvailableMetricKeys!);
        Assert.Contains(result.Dataset.Records, r => r.Likes == 0); // daily zeros attached
    }

    // ------------------------------------------------------------------
    // All-or-nothing fallbacks (list-based period totals, daily stays null)
    // ------------------------------------------------------------------

    [Fact]
    public async Task BatchDaily_TrafficFieldsAbsentEverywhere_FallsBackToListTotals()
    {
        var window = Window(10, 19, 10, 22);

        // Stateful list responder: page 1 has a token, page 2 does not.
        var (provider2, handler2, videoService2, db2) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return Ok(OverviewJson(DateTime.Parse(query["start_date_ge"]!), DateTime.Parse(query["end_date_lt"]!)));
            }

            if (path == ListPath)
            {
                var hasToken = request.RequestUri.Query.Contains("page_token");
                return Ok(hasToken
                    ? ListPage(null, null, ListVideo("L-2", 5, 5, 5))
                    : ListPage("NEXT", null, ListVideo("L-1", 10, 1, 2)));
            }

            return Ok("{\"code\":0,\"data\":{\"videos\":[]}}");
        });
        await SeedVideosAsync(db2, ("V-1", new DateTime(2026, 10, 10)));
        videoService2.Responder = (_, _, _) => BatchOk(BatchEntry("V-1")); // entry without traffic

        var result = await provider2.GetAsync(window);

        Assert.True(result.IsAvailable);
        var totals = result.Dataset!.PeriodInteractions!;
        Assert.Equal(15, totals.Likes);
        Assert.Equal(6, totals.Comments);
        Assert.Equal(7, totals.Shares);
        Assert.Equal(2, totals.VideoCount);
        Assert.All(result.Dataset.Records, r => Assert.Null(r.Likes)); // no daily breakdown from list totals
        Assert.Single(videoService2.Calls);
        // 1 catalog-coverage probe (runs BEFORE the batch fan-out) + 2 pagination pages.
        Assert.Equal(3, handler2.ListRequestCount);
    }

    [Fact]
    public async Task BatchDaily_LookbackRejection_FallsBackToListTotals()
    {
        var window = Window(10, 19, 10, 22);
        var (provider, handler, videoService, db) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return Ok(OverviewJson(DateTime.Parse(query["start_date_ge"]!), DateTime.Parse(query["end_date_lt"]!)));
            }

            return Ok(ListPage(null, null, ListVideo("L-1", 10, 1, 2)));
        });
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 10, 10)));
        videoService.Responder = (_, _, _) => new TikTokBatchPerformanceResult(
            200, 28001022,
            "Invalid Parameter. Parameter `start_date_ge and end_date_lt` is invalid because start_date_ge must be earlier than end_date_lt and must stay within the lookback window supported by this API version in the shop timezone.",
            "req-400", Array.Empty<JsonElement>());

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        var totals = result.Dataset!.PeriodInteractions!;
        Assert.Equal(10, totals.Likes);
        Assert.All(result.Dataset.Records, r => Assert.Null(r.Likes));
    }

    [Fact]
    public async Task BatchDaily_ChunkFailure_FallsBack_NoPartialTotals()
    {
        var window = Window(10, 19, 10, 22);
        var (provider, handler, videoService, db) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return Ok(OverviewJson(DateTime.Parse(query["start_date_ge"]!), DateTime.Parse(query["end_date_lt"]!)));
            }

            return Ok(ListPage(null, null, ListVideo("L-1", 4, 4, 4)));
        });

        // 501 local videos -> 2 chunks (500 + 1); the second chunk fails with HTTP 500.
        var videos = Enumerable.Range(1, 501)
            .Select(i => ($"B-{i:D4}", (DateTime?)new DateTime(2026, 10, 10)))
            .ToArray();
        await SeedVideosAsync(db, videos);

        var d19 = new DateTime(2026, 10, 19);
        videoService.Responder = (ids, _, _) => ids.Count > 1
            ? BatchOk(BatchEntry("B-0001", DailyInterval(d19, likes: 100)))
            : new TikTokBatchPerformanceResult(500, null, null, null, Array.Empty<JsonElement>());

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        var totals = result.Dataset!.PeriodInteractions!;
        // The list totals (4) win; the partial batch sum (100) must never surface.
        Assert.Equal(4, totals.Likes);
        Assert.All(result.Dataset.Records, r => Assert.Null(r.Likes));
        Assert.Equal(2, videoService.Calls.Count);
    }

    [Fact]
    public async Task BatchDaily_CatalogCoverageMismatch_FallsBackToList()
    {
        var window = Window(10, 19, 10, 22);
        var (provider, handler, videoService, db) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return Ok(OverviewJson(DateTime.Parse(query["start_date_ge"]!), DateTime.Parse(query["end_date_lt"]!)));
            }

            // The API catalog holds 10 videos; our local catalog only knows 3.
            return Ok(ListPage(null, "10", ListVideo("L-1", 8, 8, 8)));
        });
        await SeedVideosAsync(db,
            ("V-1", new DateTime(2026, 10, 10)),
            ("V-2", new DateTime(2026, 10, 11)),
            ("V-3", new DateTime(2026, 10, 12)));

        var d19 = new DateTime(2026, 10, 19);
        videoService.Responder = (ids, _, _) => BatchOk(
            BatchEntry(ids[0], DailyInterval(d19, likes: 1, comments: 1, shares: 1)),
            BatchEntry(ids[1], DailyInterval(d19, likes: 1)),
            BatchEntry(ids[2], DailyInterval(d19, likes: 1)));

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        var totals = result.Dataset!.PeriodInteractions!;
        // List totals (8) win; the understated batch sum (3) must never surface.
        Assert.Equal(8, totals.Likes);
        Assert.All(result.Dataset.Records, r => Assert.Null(r.Likes));
    }

    [Fact]
    public async Task BatchDaily_FanOutBudgetExceeded_SkipsBatchEntirely()
    {
        // 113-day window -> 17 sub-windows x 1 chunk = 17 requests > budget 16.
        var window = new AccountDailyMetricQuery
        {
            Start = new DateTime(2026, 7, 1),
            EndExclusive = new DateTime(2026, 10, 22)
        };
        var (provider, handler, videoService, db) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return Ok(OverviewJson(DateTime.Parse(query["start_date_ge"]!), DateTime.Parse(query["end_date_lt"]!)));
            }

            return Ok(ListPage(null, null, ListVideo("L-1", 6, 6, 6)));
        });
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 7, 2)));

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        Assert.Empty(videoService.Calls); // batch never ran
        Assert.Equal(6, result.Dataset!.PeriodInteractions!.Likes);
        Assert.All(result.Dataset.Records, r => Assert.Null(r.Likes));
    }

    // ------------------------------------------------------------------
    // Sub-window splitting (VERIFIED: granularity=1D accepts at most 7-day spans)
    // ------------------------------------------------------------------

    [Fact]
    public async Task BatchDaily_LongWindow_SplitsIntoSevenDaySubWindows()
    {
        var window = Window(10, 1, 10, 15); // 14 days -> 2 sub-windows
        var (provider, _, videoService, db) = CreateProvider(OverviewAndList());
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 9, 1)));

        videoService.Responder = (_, start, _) => BatchOk(
            BatchEntry("V-1", DailyInterval(start, likes: start.Day == 1 ? 4 : 6)));

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        Assert.Equal(2, videoService.Calls.Count);
        Assert.Equal(new DateTime(2026, 10, 1), videoService.Calls[0].Start);
        Assert.Equal(new DateTime(2026, 10, 8), videoService.Calls[0].End);
        Assert.Equal(new DateTime(2026, 10, 8), videoService.Calls[1].Start);
        Assert.Equal(new DateTime(2026, 10, 15), videoService.Calls[1].End);
        Assert.All(videoService.Calls, c => Assert.Equal("1D", c.Granularity));

        var totals = result.Dataset!.PeriodInteractions!;
        Assert.Equal(10, totals.Likes); // 4 (Oct 1) + 6 (Oct 8)
        var records = result.Dataset.Records.ToDictionary(r => r.Date);
        Assert.Equal(4, records[new DateTime(2026, 10, 1)].Likes);
        Assert.Equal(6, records[new DateTime(2026, 10, 8)].Likes);
        Assert.Null(records[new DateTime(2026, 10, 2)].Likes);
    }

    // ------------------------------------------------------------------
    // Cache: one TikTok fan-out per (shop, window) inside the TTL
    // ------------------------------------------------------------------

    [Fact]
    public async Task BatchDaily_Cached_SecondRenderMakesNoNewRequests()
    {
        var window = Window(10, 19, 10, 22);
        var cache = new TikTokPeriodInteractionsCache();
        var (provider, handler, videoService, db) = CreateProvider(OverviewAndList(), cache);
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 10, 10)));

        var d19 = new DateTime(2026, 10, 19);
        videoService.Responder = (_, _, _) => BatchOk(BatchEntry("V-1", DailyInterval(d19, likes: 9, comments: 1, shares: 1)));

        var first = await provider.GetAsync(window);
        var callsAfterFirst = videoService.Calls.Count;
        var listAfterFirst = handler.ListRequestCount;

        var second = await provider.GetAsync(window);

        Assert.True(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.Equal(9, second.Dataset!.PeriodInteractions!.Likes);
        Assert.Equal(callsAfterFirst, videoService.Calls.Count); // no new batch requests
        Assert.Equal(listAfterFirst, handler.ListRequestCount);  // no new completeness probes
        Assert.Equal(9, second.Dataset.Records.Single(r => r.Date == d19).Likes);
    }

    // ------------------------------------------------------------------
    // ROOT-CAUSE FIX (2026-10-23): previous-period window must stay on the batch path
    // ------------------------------------------------------------------

    [Fact]
    public async Task BatchDaily_PreviousWindow_SkipsNewerVideos_WithoutFallingBackToList()
    {
        // ROOT-CAUSE regression test for "Interactions PERIODE SEBELUMNYA tetap -":
        // the old catalog-coverage check compared the ids RETURNED by the batch
        // (videos posted before the window end - a subset for any past window) with
        // the list API's WHOLE-catalog total_count, so a previous period could never
        // pass once the shop posted anything newer. It then degraded to the ~28-page
        // list fallback (fragile against the shared rate limit) and, when that failed,
        // rendered "-". The aligned check compares WHOLE local catalog vs WHOLE API
        // catalog, so a past window now stays on the cheap batch path.
        var previousWindow = Window(10, 12, 10, 15); // a PAST window
        var (provider, handler, videoService, db) = CreateProvider(
            OverviewAndList(() => Ok(ListPage(null, "3", // API holds exactly the 3 videos we know
                ListVideo("V-1", 0, 0, 0), ListVideo("V-2", 0, 0, 0), ListVideo("V-3", 0, 0, 0)))));

        // V-3 was posted AFTER the previous window end: legitimately excluded from the
        // batch ids, but part of the whole-catalog total.
        await SeedVideosAsync(db,
            ("V-1", new DateTime(2026, 10, 1)),
            ("V-2", new DateTime(2026, 10, 5)),
            ("V-3", new DateTime(2026, 10, 20)));

        var d13 = new DateTime(2026, 10, 13);
        videoService.Responder = (ids, start, _) =>
        {
            Assert.Equal(2, ids.Count); // V-3 (newer than the window) is never queried
            Assert.DoesNotContain("V-3", ids);
            return BatchOk(
                BatchEntry("V-1", DailyInterval(d13, likes: 4, comments: 1, shares: 1)),
                BatchEntry("V-2", DailyInterval(d13, likes: 2)));
        };

        var result = await provider.GetAsync(previousWindow);

        Assert.True(result.IsAvailable);
        var totals = result.Dataset!.PeriodInteractions!;
        Assert.Equal(6, totals.Likes);   // batch values, NOT the list totals
        Assert.Equal(1, totals.Comments);
        Assert.Equal(1, totals.Shares);
        Assert.Equal(2, totals.VideoCount);

        // Daily breakdown lands on the record of that past day (DETAIL HARIAN):
        // the day's record aggregates BOTH videos (4 + 2 likes).
        var record = result.Dataset.Records.Single(r => r.Date == d13);
        Assert.Equal(6, record.Likes);
        Assert.Equal(1, record.Comments);
        Assert.Equal(1, record.Shares);

        // NO pagination fallback: the only list request is the one coverage probe.
        Assert.Equal(1, handler.ListRequestCount);
        Assert.Single(videoService.Calls);
    }

    [Fact]
    public async Task BatchDaily_CacheCurrentAndPreviousWindows_NeverShareEntries()
    {
        // Cache keys must separate (shop, start, end-exclusive): the current and the
        // previous window of one report carry DIFFERENT values and must never collide.
        var cache = new TikTokPeriodInteractionsCache();
        var (provider, _, videoService, db) = CreateProvider(OverviewAndList(), cache);
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 10, 1)));

        var previousWindow = Window(10, 12, 10, 15);
        var currentWindow = Window(10, 19, 10, 22);

        videoService.Responder = (_, start, _) =>
        {
            var isPrevious = start < new DateTime(2026, 10, 19);
            return BatchOk(BatchEntry("V-1",
                DailyInterval(isPrevious ? new DateTime(2026, 10, 13) : new DateTime(2026, 10, 19),
                    likes: isPrevious ? 3 : 30)));
        };

        var previous = await provider.GetAsync(previousWindow);
        var current = await provider.GetAsync(currentWindow);

        Assert.Equal(3, previous.Dataset!.PeriodInteractions!.Likes);
        Assert.Equal(30, current.Dataset!.PeriodInteractions!.Likes);

        // Both outcomes live in the SAME cache instance under distinct keys: re-asking
        // each window returns its OWN values (no cross-window contamination) and makes
        // no new batch request.
        var previousAgain = await provider.GetAsync(previousWindow);
        var currentAgain = await provider.GetAsync(currentWindow);
        Assert.Equal(3, previousAgain.Dataset!.PeriodInteractions!.Likes);
        Assert.Equal(30, currentAgain.Dataset!.PeriodInteractions!.Likes);
        Assert.Single(videoService.Calls.Where(c => c.Start == previousWindow.Start));
        Assert.Single(videoService.Calls.Where(c => c.Start == currentWindow.Start));
    }

    // ------------------------------------------------------------------
    // Per-video errors (verified contract field data.videos[].error)
    // ------------------------------------------------------------------

    [Fact]
    public async Task BatchDaily_PerVideoError_FallsBackToListTotals_NeverPartialSum()
    {
        // The envelope code=0 does NOT mean every video succeeded: an entry carrying
        // data.videos[].error has UNKNOWN interactions, so the whole daily outcome
        // must fall back to the authoritative list totals instead of surfacing a
        // partial sum that silently omits the failed video.
        var window = Window(10, 19, 10, 22);
        var (provider, _, videoService, db) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return Ok(OverviewJson(DateTime.Parse(query["start_date_ge"]!), DateTime.Parse(query["end_date_lt"]!)));
            }

            return Ok(ListPage(null, null, ListVideo("L-1", 10, 1, 2)));
        });
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 10, 10)), ("V-2", new DateTime(2026, 10, 10)));

        var d19 = new DateTime(2026, 10, 19);
        videoService.Responder = (_, _, _) => BatchOk(
            BatchEntry("V-1", DailyInterval(d19, likes: 100)),
            "{\"video_id\":\"V-2\",\"error\":{\"code\":\"28002001\",\"message\":\"video not found\"}}");

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        var totals = result.Dataset!.PeriodInteractions!;
        // The list totals (10) win; the partial batch sum (100) must never surface.
        Assert.Equal(10, totals.Likes);
        Assert.All(result.Dataset.Records, r => Assert.Null(r.Likes));
    }

    // ------------------------------------------------------------------
    // DETAIL HARIAN completeness: batch-only days become interactions-only records
    // ------------------------------------------------------------------

    [Fact]
    public async Task BatchDaily_DayWithoutOverviewInterval_BecomesInteractionsOnlyRecord()
    {
        // The overview endpoint omits 21 Oct (no interval), but the batch endpoint
        // reports real interactions for that date. That day must still appear in the
        // dataset (daily table) with its interactions - every other field stays null.
        var window = Window(10, 19, 10, 22);
        var (provider, _, videoService, db) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                var start = DateTime.Parse(query["start_date_ge"]!);
                var end = DateTime.Parse(query["end_date_lt"]!);
                // Overview intervals for 19 and 20 Oct ONLY (21 Oct missing).
                var intervals = new List<string>
                {
                    "{\"start_date\":\"2026-10-19\",\"end_date\":\"2026-10-20\",\"video_views\":1000}",
                    "{\"start_date\":\"2026-10-20\",\"end_date\":\"2026-10-21\",\"video_views\":1000}"
                };
                return Ok("{\"code\":0,\"message\":\"Success\",\"data\":{\"latest_available_date\":\""
                    + Today.AddDays(-1).ToString("yyyy-MM-dd")
                    + "\",\"performance\":{\"intervals\":[" + string.Join(",", intervals) + "]}}}");
            }

            return Ok("{\"code\":0,\"data\":{\"videos\":[]}}"); // no catalog total exposed
        });
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 10, 10)));

        videoService.Responder = (_, _, _) => BatchOk(
            BatchEntry("V-1",
                DailyInterval(new DateTime(2026, 10, 19), likes: 5, comments: 1, shares: 1),
                DailyInterval(new DateTime(2026, 10, 21), likes: 7, comments: 2, shares: 1)));

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        var records = result.Dataset!.Records.ToDictionary(r => r.Date);

        // Overview day: normal record carrying BOTH shop metrics and interactions.
        Assert.Equal(1000L, records[new DateTime(2026, 10, 19)].VideoViews);
        Assert.Equal(5, records[new DateTime(2026, 10, 19)].Likes);

        // Batch-only day: interactions-only record, other fields stay null (honest "-").
        var batchOnly = records[new DateTime(2026, 10, 21)];
        Assert.Equal(7, batchOnly.Likes);
        Assert.Equal(2, batchOnly.Comments);
        Assert.Equal(1, batchOnly.Shares);
        Assert.Null(batchOnly.VideoViews);
        Assert.Null(batchOnly.ProductImpressions);

        // Period totals cover BOTH days.
        Assert.Equal(12, result.Dataset.PeriodInteractions!.Likes);
    }

    [Fact]
    public async Task BatchDaily_BatchOnlyDayWithNoTrafficAtAll_CreatesNoPhantomRecord()
    {
        var window = Window(10, 19, 10, 22);
        var (provider, _, videoService, db) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return Ok("{\"code\":0,\"message\":\"Success\",\"data\":{\"latest_available_date\":\""
                    + Today.AddDays(-1).ToString("yyyy-MM-dd")
                    + "\",\"performance\":{\"intervals\":["
                    + "{\"start_date\":\"2026-10-19\",\"end_date\":\"2026-10-20\",\"video_views\":1000}]}}}");
            }

            return Ok("{\"code\":0,\"data\":{\"videos\":[]}}");
        });
        await SeedVideosAsync(db, ("V-1", new DateTime(2026, 10, 10)));

        // The batch returns a 20 Oct interval whose traffic carries NO likes/comments/shares
        // fields: an all-null day must never become a fabricated record.
        videoService.Responder = (_, _, _) => BatchOk(
            BatchEntry("V-1",
                DailyInterval(new DateTime(2026, 10, 19), likes: 5),
                "{\"start_date\":\"2026-10-20\",\"end_date\":\"2026-10-21\",\"traffic\":{\"views\":10}}"));

        var result = await provider.GetAsync(window);

        Assert.True(result.IsAvailable);
        Assert.DoesNotContain(result.Dataset!.Records, r => r.Date == new DateTime(2026, 10, 20));
        Assert.Equal(5, result.Dataset.PeriodInteractions!.Likes);
    }
}
