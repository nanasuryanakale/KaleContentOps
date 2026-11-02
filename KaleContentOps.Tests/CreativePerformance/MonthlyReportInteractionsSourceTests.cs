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
/// END-TO-END reproduction of the reported Monthly Report defect: the REAL provider
/// (TikTokShopVideoOverviewMetricSource) feeding the REAL MonthlyPerformanceService,
/// with the paginated video LIST endpoint permanently rate-limited (HTTP 429) and the
/// batch endpoint healthy - the asymmetry observed live, where the overview endpoint
/// keeps answering (Video Views) but the interactions list path does not.
///
/// Root cause under test: the Monthly Report queries the provider ONCE PER CALENDAR
/// MONTH, so every month bucket is 29..31 days. A 16..29 Oktober window is only 14
/// days (4 chunks x 2 sub-windows = 8 requests, inside the old 16-request budget), so
/// it stayed on the batch path and kept its 8.135 - while every full-month bucket
/// exceeded the budget, silently degraded to the rate-limited list path and lost
/// Interactions (KPI "-" and an empty donut) even though Video Views still worked.
/// After the fix a full month stays on the batch (Daily/Weekly) path.
///
/// All numbers are produced by the fake TikTok responses - no production sync is run.
/// </summary>
public class MonthlyReportInteractionsSourceTests
{
    private const string OverviewPath = "/analytics/202609/shop_videos/overview_performance";
    private const string ListPath = "/analytics/202605/shop_videos/performance";

    /// <summary>Local catalog large enough that a full month needs 4 chunks (500 each).</summary>
    private const int CatalogSize = 1501;

    private static readonly DateTime CatalogPostTime = new(2026, 9, 5);

    // ------------------------------------------------------------------
    // Per-window verified totals (one payout per queried window, so the same
    // window is never reused as another month's data).
    // ------------------------------------------------------------------

    /// <summary>
    /// Verified totals for the FIRST sub-window of a queried window (the batch path
    /// is asked once per 7-day sub-window, so each sub-window pays exactly once).
    /// Every other sub-window of the same report window pays a verified 0 - the totals
    /// therefore describe exactly one window and are never reused for another month.
    /// The 16 Oktober window reproduces the reported 8.135 (80,58% / 2,79% / 16,63%).
    /// </summary>
    private static (long Likes, long Comments, long Shares) WindowTotals(DateTime subWindowStart)
    {
        if (subWindowStart == new DateTime(2026, 10, 16)) return (6555, 227, 1353); // reported: 8.135
        if (subWindowStart == new DateTime(2026, 10, 1)) return (4000, 100, 900);
        if (subWindowStart == new DateTime(2026, 9, 1)) return (1000, 400, 600);
        return (0, 0, 0); // any other (previous-period) window contributes a verified zero
    }

    // ------------------------------------------------------------------
    // Verified response shapes
    // ------------------------------------------------------------------

    private static string OverviewJson(DateTime start, DateTime endExclusive)
    {
        var intervals = new List<string>();
        for (var day = start; day < endExclusive; day = day.AddDays(1))
        {
            intervals.Add($"{{\"start_date\":\"{day:yyyy-MM-dd}\",\"end_date\":\"{day.AddDays(1):yyyy-MM-dd}\",\"video_views\":1000}}");
        }

        return "{\"code\":0,\"message\":\"Success\",\"request_id\":\"req-overview\",\"data\":{"
            + "\"latest_available_date\":\"2026-10-31\","
            + "\"performance\":{\"intervals\":[" + string.Join(",", intervals) + "]}}}";
    }

    private static string BatchEntry(string id, string interval)
        => "{\"video_id\":\"" + id + "\",\"performance\":{\"intervals\":[" + interval + "]}}";

    private static string DailyInterval(DateTime date, long likes, long comments, long shares)
        => "{\"start_date\":\"" + date.ToString("yyyy-MM-dd") + "\",\"end_date\":\""
        + date.AddDays(1).ToString("yyyy-MM-dd")
        + "\",\"traffic\":{\"likes\":" + likes + ",\"comments\":" + comments + ",\"shares\":" + shares + ",\"views\":10}}";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Verified list-page shape: data.videos[] carries windowed likes/comments/shares.</summary>
    private static string ListPageJson(params string[] videos)
        => "{\"code\":0,\"message\":\"Success\",\"request_id\":\"req-list\",\"data\":{\"videos\":["
            + string.Join(",", videos) + "]}}";

    private static string ListVideo(string id, long likes, long comments, long shares)
        => "{\"id\":\"" + id + "\",\"likes\":" + likes + ",\"comments\":" + comments + ",\"shares\":" + shares + "}";

    private static TikTokBatchPerformanceResult BatchOk(params string[] entries)
    {
        using var doc = JsonDocument.Parse(
            "{\"code\":0,\"message\":\"Success\",\"request_id\":\"req-batch\",\"data\":{\"videos\":[" + string.Join(",", entries) + "]}}");
        return new TikTokBatchPerformanceResult(200, 0, "Success", "req-batch",
            doc.RootElement.GetProperty("data").GetProperty("videos").EnumerateArray().Select(e => e.Clone()).ToList());
    }

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
            get { lock (Requests) { return Requests.Count(r => r.RequestUri!.AbsolutePath == ListPath); } }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) { Requests.Add(request); }
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
            IReadOnlyList<string> videoIds, string shopCipher,
            DateTime startDateInclusive, DateTime endDateExclusive, string? granularity,
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

        public Task<(int? StatusCode, string Endpoint, JsonElement? Root, JsonElement? Data, int? RetryAfterSeconds)> RunDetailsDiagnosticAsync(string videoId, string shopCipher, CancellationToken cancellationToken = default, DateTime? startDateInclusive = null, DateTime? endDateExclusive = null)
            => throw new NotSupportedException();
    }

    private sealed class FakeShopTimeZone : IShopTimeZone
    {
        public string TimeZoneId => ShopTimeZoneOptions.DefaultTimeZoneId;
        public DateTimeOffset ToShopLocal(DateTimeOffset utcInstant) => utcInstant;
        public DateOnly GetShopLocalDate(DateTimeOffset utcInstant) => DateOnly.FromDateTime(utcInstant.Date);
        public DateOnly Today() => new(2026, 10, 29);
        public DateTime ToDateTime(DateOnly shopLocalDate) => shopLocalDate.ToDateTime(new TimeOnly(0, 0));
        public DateTime TodayMidnight() => ToDateTime(Today());
    }

    /// <summary>
    /// Provider + service wired exactly like production, with the LIST endpoint
    /// permanently rate-limited (HTTP 429) and the batch endpoint healthy - unless a
    /// test passes its own list responder or a different catalog size.
    /// </summary>
    private static (TikTokShopVideoOverviewMetricSource Provider, MonthlyPerformanceService Service, RoutingHandler Handler, FakeVideoService VideoService) CreateReport(
        Func<HttpRequestMessage, HttpResponseMessage>? listResponder = null,
        int? catalogSize = null)
    {
        var size = catalogSize ?? CatalogSize;
        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == OverviewPath)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                return Json(OverviewJson(
                    DateTime.Parse(query["start_date_ge"]!),
                    DateTime.Parse(query["end_date_lt"]!)));
            }

            // The shared app-group rate limit: real 429 observed against this endpoint.
            return listResponder is not null
                ? listResponder(request)
                : Json("{\"code\":429,\"message\":\"Rate limit exceeded\"}", HttpStatusCode.TooManyRequests);
        });

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

        var videos = Enumerable.Range(1, size)
            .Select(i => new ContentLog
            {
                VideoId = $"V-{i:D4}",
                TikTokShopId = db.TikTokShops.First().Id,
                ProductionMethodId = 1,
                VideoPostTime = CatalogPostTime
            });
        db.ContentLogs.AddRange(videos);
        db.SaveChanges();

        var options = Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" });
        var videoService = new FakeVideoService();

        // One payout per (window, sub-window): the batch path is asked once per
        // 7-day sub-window per video chunk, so each distinct sub-window start pays
        // exactly once and later chunks of the same sub-window contribute nothing.
        var paidStarts = new HashSet<DateTime>();
        videoService.Responder = (ids, start, _) =>
        {
            if (paidStarts.Add(start))
            {
                var (likes, comments, shares) = WindowTotals(start);
                return BatchOk(BatchEntry(ids[0], DailyInterval(start, likes, comments, shares)));
            }

            return BatchOk(); // later chunks of the same sub-window contribute nothing new
        };

        var provider = new TikTokShopVideoOverviewMetricSource(
            factory, options, db, new TikTokSignatureService(options), new StubAuth(),
            videoService: videoService);

        return (provider, new MonthlyPerformanceService(provider, new FakeShopTimeZone()), handler, videoService);
    }

    private static MonthlyReportFilter Filter(int startMonth, int startDay, int endMonth, int endDay)
        => new() { StartDate = new DateTime(2026, startMonth, startDay), EndDate = new DateTime(2026, endMonth, endDay) };

    private static DailyReportMetric Card(MonthlyReportData data, string key)
        => data.Cards.Single(c => c.Key == key);

    // ------------------------------------------------------------------
    // Source level: which path does a month bucket take?
    // ------------------------------------------------------------------

    [Fact]
    public async Task Source_FullMonthWindow_StaysOnBatchPath_WhenListIsRateLimited()
    {
        var (provider, _, handler, videoService) = CreateReport();

        // 1..30 September = 30 days -> 5 sub-windows x 4 chunks = 20 requests.
        var result = await provider.GetAsync(new AccountDailyMetricQuery
        {
            Start = new DateTime(2026, 9, 1),
            EndExclusive = new DateTime(2026, 10, 1)
        });

        Assert.True(result.IsAvailable);
        Assert.NotNull(result.Dataset!.PeriodInteractions);
        Assert.Equal(1000, result.Dataset.PeriodInteractions!.Likes);
        Assert.Equal(400, result.Dataset.PeriodInteractions.Comments);
        Assert.Equal(600, result.Dataset.PeriodInteractions.Shares);
        Assert.Contains(DailyReportMetrics.Interactions, result.Dataset.AvailableMetricKeys!);
        Assert.Contains(DailyReportMetrics.VideoViews, result.Dataset.AvailableMetricKeys!);

        // The batch path ran for the whole month (20 requests) and the list endpoint
        // was only touched by the single catalog-coverage probe - never paginated.
        Assert.Equal(20, videoService.Calls.Count);
        Assert.Equal(1, handler.ListRequestCount);
    }

    [Fact]
    public async Task Source_WindowWiderThanOneMonth_StillDegradesToListTotals()
    {
        var (provider, _, handler, videoService) = CreateReport();

        // 1 Jul .. 1 Nov = 124 days: wider than the month bound, so the fan-out
        // stays bounded and the list totals remain the fallback (existing contract).
        var result = await provider.GetAsync(new AccountDailyMetricQuery
        {
            Start = new DateTime(2026, 7, 1),
            EndExclusive = new DateTime(2026, 11, 1)
        });

        Assert.True(result.IsAvailable);
        Assert.Empty(videoService.Calls); // batch never ran
        Assert.Contains(DailyReportMetrics.VideoViews, result.Dataset!.AvailableMetricKeys!);
    }

    // ------------------------------------------------------------------
    // The four reported scenarios
    // ------------------------------------------------------------------

    [Fact]
    public async Task ScenarioA_16To29October_KeepsInteractions8135_AndComposition()
    {
        var (_, service, handler, _) = CreateReport();

        var data = await service.BuildAsync(Filter(10, 16, 10, 29));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(8135m, interactions.CurrentValue);
        Assert.True(interactions.IsAvailable);

        var composition = data.InteractionComposition;
        Assert.True(composition.IsAvailable);
        Assert.Equal(8135m, composition.Total);
        Assert.Equal(80.58m, Math.Round(composition.Segments.Single(s => s.Key == "like").Percentage!.Value, 2));
        Assert.Equal(2.79m, Math.Round(composition.Segments.Single(s => s.Key == "comment").Percentage!.Value, 2));
        Assert.Equal(16.63m, Math.Round(composition.Segments.Single(s => s.Key == "share").Percentage!.Value, 2));

        // KPI == table row == donut total, and Video Views is present too.
        Assert.Equal(8135m, data.MonthlyRows.Sum(r => r.Interactions ?? 0));
        Assert.NotNull(Card(data, DailyReportMetrics.VideoViews).CurrentValue);

        // 2 windows (current + previous) x 1 catalog probe - no list pagination.
        Assert.Equal(2, handler.ListRequestCount);
    }

    [Fact]
    public async Task ScenarioB_1To29October_SingleMonthAggregationStaysCorrect()
    {
        var (_, service, handler, videoService) = CreateReport();

        var data = await service.BuildAsync(Filter(10, 1, 10, 29));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(5000m, interactions.CurrentValue); // 4000 + 100 + 900
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);

        var composition = data.InteractionComposition;
        Assert.True(composition.IsAvailable);
        Assert.Equal(5000m, composition.Total);
        Assert.Equal(4000m, composition.Segments.Single(s => s.Key == "like").Value);
        Assert.Equal(100m, composition.Segments.Single(s => s.Key == "comment").Value);
        Assert.Equal(900m, composition.Segments.Single(s => s.Key == "share").Value);

        Assert.Single(data.MonthlyRows);
        Assert.Equal(5000m, data.MonthlyRows.Sum(r => r.Interactions ?? 0));

        // The 29-day month bucket ran 5 seven-day sub-windows x 4 video chunks = 20
        // batch requests (plus 20 for the equal-length previous window).
        Assert.Equal(40, videoService.Calls.Count);
        Assert.Equal(5, videoService.Calls.Where(c => c.Start.Month == 10).Select(c => c.Start).Distinct().Count());
        Assert.Equal(2, handler.ListRequestCount); // current + previous probe only
    }

    [Fact]
    public async Task ScenarioC_1To30September_InteractionsAvailable()
    {
        var (_, service, handler, _) = CreateReport();

        var data = await service.BuildAsync(Filter(9, 1, 9, 30));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(2000m, interactions.CurrentValue); // 1000 + 400 + 600
        Assert.True(interactions.IsAvailable);

        var composition = data.InteractionComposition;
        Assert.True(composition.IsAvailable);
        Assert.Equal(50m, composition.Segments.Single(s => s.Key == "like").Percentage);
        Assert.Equal(20m, composition.Segments.Single(s => s.Key == "comment").Percentage);
        Assert.Equal(30m, composition.Segments.Single(s => s.Key == "share").Percentage);

        Assert.NotNull(Card(data, DailyReportMetrics.VideoViews).CurrentValue);
        Assert.Equal(2, handler.ListRequestCount);
    }

    [Fact]
    public async Task ScenarioD_1SeptemberTo29October_AggregatesBothMonths_WithCompositionFromTotals()
    {
        var (_, service, handler, videoService) = CreateReport();

        var data = await service.BuildAsync(Filter(9, 1, 10, 29));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(7000m, interactions.CurrentValue); // 2000 (Sep) + 5000 (Okt)
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);
        Assert.True(interactions.IsAvailable);

        Assert.Equal(2, data.MonthlyRows.Count);
        Assert.Equal(2000L, data.MonthlyRows.Single(r => r.Start.Month == 9).Interactions);
        Assert.Equal(5000L, data.MonthlyRows.Single(r => r.Start.Month == 10).Interactions);
        Assert.Equal(7000m, data.MonthlyRows.Sum(r => r.Interactions ?? 0));

        var composition = data.InteractionComposition;
        Assert.True(composition.IsAvailable);
        Assert.Equal(7000m, composition.Total);
        // AGGREGATE totals (5000/7000 = 71,4286%), never the average of the two
        // monthly like rates ((50% + 80%) / 2 = 65%).
        Assert.Equal(71.4286m, composition.Segments.Single(s => s.Key == "like").Percentage);
        Assert.Equal(7.1429m, composition.Segments.Single(s => s.Key == "comment").Percentage);
        Assert.Equal(21.4286m, composition.Segments.Single(s => s.Key == "share").Percentage);
        Assert.Equal(7000m, composition.Segments.Sum(s => s.Value));

        // Video Views keeps working for the same two-month period.
        Assert.NotNull(Card(data, DailyReportMetrics.VideoViews).CurrentValue);

        // 4 month windows (2 current + 2 previous) x 1 catalog probe: the report never
        // paginates the rate-limited list endpoint any more.
        Assert.Equal(4, handler.ListRequestCount);
        Assert.Equal(40, videoService.Calls.Count); // 20 (Sep) + 20 (Okt)
    }

    // ------------------------------------------------------------------
    // Fan-out plan, catalog cap and batch -> list fallback diagnosis
    // ------------------------------------------------------------------

    [Fact]
    public void PlanFanOut_29DayWindow_WithTheLiveCatalog_MatchesTheLoggedNumbers()
    {
        // Live catalog measured on 2026-10-30: 12.338 local videos, period 1..29 Okt
        // (29 days, EndExclusive 30 Okt) - the Scenario B window.
        var plan = TikTokShopVideoOverviewMetricSource.PlanFanOut(
            new DateTime(2026, 10, 1), new DateTime(2026, 10, 30), 12338);

        Assert.Equal(29, plan.SpanDays);
        Assert.Equal(5, plan.SubWindows);          // 7 + 7 + 7 + 7 + 1 days
        Assert.Equal(6000, plan.CappedVideos);     // BatchMaxVideos
        Assert.Equal(12, plan.Chunks);             // 6000 / 500
        Assert.Equal(60, plan.FanOutRequests);     // 12 x 5 - exactly the budget
        Assert.True(plan.ExceedsCatalogCap);       // 12.338 > 6.000 -> list totals
        Assert.False(plan.ExceedsSpanBound);
        Assert.False(plan.ExceedsBudget);

        // The 14-day window that always worked (1501-video catalog).
        var small = TikTokShopVideoOverviewMetricSource.PlanFanOut(
            new DateTime(2026, 10, 16), new DateTime(2026, 10, 30), 1501);
        Assert.Equal(14, small.SpanDays);
        Assert.Equal(2, small.SubWindows);
        Assert.Equal(4, small.Chunks);
        Assert.Equal(8, small.FanOutRequests);
        Assert.False(small.ExceedsCatalogCap);
        Assert.False(small.ExceedsBudget);

        // An oversized span still degrades (existing contract).
        var wide = TikTokShopVideoOverviewMetricSource.PlanFanOut(
            new DateTime(2026, 7, 1), new DateTime(2026, 11, 1), 1501);
        Assert.True(wide.ExceedsSpanBound);
    }

    [Fact]
    public async Task Source_CatalogAboveBatchCap_DegradesToList_NeverAPartialBatchSum()
    {
        // 6.001 eligible videos: the batch guard allows 6.000, so a batch sum would
        // silently miss videos. The window must degrade to the list totals instead.
        var (provider, _, handler, videoService) = CreateReport(
            listResponder: _ => Json(ListPageJson(ListVideo("V-0001", 700, 30, 270))),
            catalogSize: 6001);

        var result = await provider.GetAsync(new AccountDailyMetricQuery
        {
            Start = new DateTime(2026, 10, 1),
            EndExclusive = new DateTime(2026, 10, 30)
        });

        Assert.True(result.IsAvailable);
        Assert.Empty(videoService.Calls); // batch never ran - no partial sum

        // PeriodInteractions comes from the paginated list and the keys agree.
        Assert.NotNull(result.Dataset!.PeriodInteractions);
        Assert.Equal(700, result.Dataset.PeriodInteractions!.Likes);
        Assert.Equal(30, result.Dataset.PeriodInteractions.Comments);
        Assert.Equal(270, result.Dataset.PeriodInteractions.Shares);
        Assert.Contains(DailyReportMetrics.Interactions, result.Dataset.AvailableMetricKeys!);
        Assert.Contains(DailyReportMetrics.VideoViews, result.Dataset.AvailableMetricKeys!);

        // Only the single list page - the catalog probe is skipped because the batch
        // path was rejected before any TikTok request was planned.
        Assert.Equal(1, handler.ListRequestCount);
    }

    [Fact]
    public async Task Source_BatchRejected_HttpError_FallsBackToList_AndKeepsKeysConsistent()
    {
        var (provider, _, handler, videoService) = CreateReport(
            listResponder: _ => Json(ListPageJson(ListVideo("V-0001", 700, 30, 270))));

        // The batch endpoint answers HTTP 400 for every sub-window (the shape a
        // rejected window produces) - the failure must be diagnosable and the window
        // must still end up with list totals instead of "-".
        videoService.Responder = (_, _, _) =>
            new TikTokBatchPerformanceResult(400, null, "Invalid Parameter", "req-batch",
                Array.Empty<JsonElement>());

        var result = await provider.GetAsync(new AccountDailyMetricQuery
        {
            Start = new DateTime(2026, 10, 1),
            EndExclusive = new DateTime(2026, 10, 30)
        });

        Assert.True(result.IsAvailable);
        Assert.True(videoService.Calls.Count > 0, "batch must have been attempted before falling back");

        Assert.NotNull(result.Dataset!.PeriodInteractions);
        Assert.Equal(700, result.Dataset.PeriodInteractions!.Likes);
        Assert.Equal(30, result.Dataset.PeriodInteractions.Comments);
        Assert.Equal(270, result.Dataset.PeriodInteractions.Shares);
        Assert.Contains(DailyReportMetrics.Interactions, result.Dataset.AvailableMetricKeys!);

        // 1 catalog probe + 1 list page (the fallback).
        Assert.Equal(2, handler.ListRequestCount);
    }
}
