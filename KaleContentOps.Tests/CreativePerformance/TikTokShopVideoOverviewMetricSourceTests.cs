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
/// TikTokShopVideoOverviewMetricSource: the VERIFIED mapping of daily video_views
/// from GET /analytics/202609/shop_videos/overview_performance (API Testing Tool,
/// 2026-10-21, code=0). The six-day fixture is the verified response; everything
/// else (unrelated commerce/traffic fields, missing days, error payloads) must
/// degrade honestly - never to fabricated zeros.
/// </summary>
public class TikTokShopVideoOverviewMetricSourceTests
{
    // Verified fixture (2026-10-21): request start_date_ge=2026-10-14,
    // end_date_lt=2026-10-21, granularity=1D, currency=LOCAL, account_type=ALL,
    // today=false -> latest_available_date=2026-10-19 and six daily intervals.
    // Sum of the six days = 185.855, matching the aggregate response.
    private const string VerifiedResponseJson = """
        {
          "code": 0,
          "message": "Success",
          "request_id": "req-verified-0001",
          "data": {
            "latest_available_date": "2026-10-19",
            "performance": {
              "intervals": [
                { "start_date": "2026-10-14", "end_date": "2026-10-15", "video_views": 38771 },
                { "start_date": "2026-10-15", "end_date": "2026-10-16", "video_views": 31187 },
                { "start_date": "2026-10-16", "end_date": "2026-10-17", "video_views": 32737 },
                { "start_date": "2026-10-17", "end_date": "2026-10-18", "video_views": 28684 },
                { "start_date": "2026-10-18", "end_date": "2026-10-19", "video_views": 30595 },
                { "start_date": "2026-10-19", "end_date": "2026-10-20", "video_views": 23881 }
              ]
            }
          }
        }
        """;

    // Same interval dates, but intervals also carry unrelated commerce/traffic
    // fields that must NEVER be mapped into the Daily Report metrics.
    private const string ResponseWithUnrelatedFieldsJson = """
        {
          "code": 0,
          "message": "Success",
          "request_id": "req-verified-0002",
          "data": {
            "latest_available_date": "2026-10-19",
            "performance": {
              "intervals": [
                {
                  "start_date": "2026-10-14",
                  "end_date": "2026-10-15",
                  "video_views": 38771,
                  "video_product_viewers": 99999,
                  "product_impressions": 438,
                  "ctr": "0.0545",
                  "traffic": { "views": 477, "likes": 15, "comments": 0, "shares": 0, "new_followers": 1 },
                  "sales": { "overall": { "gmv": { "amount": "1500.00", "currency": "IDR" }, "items_sold": 3 } }
                }
              ]
            }
          }
        }
        """;

    private const string EmptyIntervalsJson = """
        {
          "code": 0,
          "message": "Success",
          "request_id": "req-verified-0003",
          "data": {
            "latest_available_date": "2026-10-13",
            "performance": { "intervals": [] }
          }
        }
        """;

    private const string BusinessErrorJson =
        """{ "code": 36009002, "message": "rate limit exceeded", "request_id": "req-36009002" }""";

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Task.FromResult(_respond());
        }
    }

    private sealed class StubAuth : ITikTokAuthService
    {
        private readonly string? _token;

        public StubAuth(string? token) => _token = token;

        public Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default)
            => Task.FromResult<TikTokTokenResponse?>(null);

        public Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult<TikTokTokenResponse?>(null);

        public Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default)
            => Task.FromResult<TikTokTokenResponse?>(null);

        public Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default)
            => Task.FromResult(_token);
    }

    private sealed class FakeShopTimeZone : IShopTimeZone
    {
        public string TimeZoneId => ShopTimeZoneOptions.DefaultTimeZoneId;

        public DateTimeOffset ToShopLocal(DateTimeOffset utcInstant) => utcInstant;

        public DateOnly GetShopLocalDate(DateTimeOffset utcInstant) => DateOnly.FromDateTime(utcInstant.Date);

        public DateOnly Today() => new(2026, 10, 21);

        public DateTime ToDateTime(DateOnly shopLocalDate) => shopLocalDate.ToDateTime(new TimeOnly(0, 0));

        public DateTime TodayMidnight() => ToDateTime(Today());
    }

    private static HttpResponseMessage OkJson(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (TikTokShopVideoOverviewMetricSource Provider, ScriptedHandler Handler, AppDbContext Db) CreateProvider(
        string responseJson,
        string? accessToken = "TEST-ACCESS-TOKEN",
        bool seedCredential = true,
        bool seedShop = true)
    {
        var handler = new ScriptedHandler(() => OkJson(responseJson));
        var factory = new SimpleHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
        });

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        if (seedCredential)
        {
            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
        }

        if (seedShop)
        {
            db.TikTokShops.Add(new TikTokShop { ShopCipher = "SHOP-CIPHER-1" });
        }

        db.SaveChanges();

        var options = Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" });
        var provider = new TikTokShopVideoOverviewMetricSource(
            factory,
            options,
            db,
            new TikTokSignatureService(options),
            new StubAuth(accessToken));

        return (provider, handler, db);
    }

    private static AccountDailyMetricQuery Window(int startMonth, int startDay, int endExclusiveMonth, int endExclusiveDay)
        => new()
        {
            Start = new DateTime(2026, startMonth, startDay),
            EndExclusive = new DateTime(2026, endExclusiveMonth, endExclusiveDay)
        };

    // ------------------------------------------------------------------
    // Verified mapping: six daily values, exact
    // ------------------------------------------------------------------

    [Fact]
    public async Task VerifiedFixture_MapsSixDailyVideoViews_WithExactValues()
    {
        var (provider, _, db) = CreateProvider(VerifiedResponseJson);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            var dataset = result.Dataset!;
            Assert.Equal(TikTokShopVideoOverviewMetricSource.ProviderSourceName, dataset.SourceName);

            // Exactly the six verified days - 2026-10-20 has no interval and must
            // NOT be fabricated as a record (zero or otherwise).
            var expected = new (int Day, long Views)[]
            {
                (14, 38771), (15, 31187), (16, 32737), (17, 28684), (18, 30595), (19, 23881)
            };
            Assert.Equal(expected.Length, dataset.Records.Count);

            foreach (var (day, views) in expected)
            {
                var record = dataset.Records.Single(r => r.Date == new DateTime(2026, 10, day));
                Assert.Equal(views, record.VideoViews);
            }

            Assert.Equal(185855, dataset.Records.Sum(r => r.VideoViews!.Value));

            // Declared scope: the only DASHBOARD summary metric mapped from this
            // endpoint is VideoViews. The verified record-level field keys are also
            // declared so callers can discover what the source actually populates
            // without parsing the response shape.
            Assert.NotNull(dataset.AvailableMetricKeys);
            var keys = dataset.AvailableMetricKeys!.OrderBy(k => k).ToList();
            Assert.Contains(DailyReportMetrics.VideoViews, keys);
            Assert.Equal(1, keys.Count(k => k == DailyReportMetrics.VideoViews));
            Assert.DoesNotContain(DailyReportMetrics.Reach, keys);
            Assert.DoesNotContain(DailyReportMetrics.ProfileViews, keys);
            Assert.DoesNotContain(DailyReportMetrics.Interactions, keys);
            Assert.DoesNotContain(DailyReportMetrics.EngagementRate, keys);
            Assert.DoesNotContain(DailyReportMetrics.NetFollower, keys);
            Assert.DoesNotContain(DailyReportMetrics.TotalFollower, keys);

            Assert.Empty(dataset.FollowerSnapshots);
            Assert.Null(dataset.ViewerComposition);
        }
    }

    [Fact]
    public async Task Request_UsesVerifiedParameters_AndHalfOpenWindow()
    {
        var (provider, handler, db) = CreateProvider(VerifiedResponseJson);
        using (db)
        {
            await provider.GetAsync(Window(10, 14, 10, 21));

            var request = Assert.Single(handler.Requests);
            var uri = request.RequestUri!;

            Assert.Equal("/analytics/202609/shop_videos/overview_performance", uri.AbsolutePath);

            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            Assert.Equal("2026-10-14", query["start_date_ge"]);
            Assert.Equal("2026-10-21", query["end_date_lt"]); // half-open: exclusive end forwarded verbatim
            Assert.Equal("1D", query["granularity"]);
            Assert.Equal("LOCAL", query["currency"]);
            Assert.Equal("ALL", query["account_type"]);
            Assert.Equal("false", query["today"]);
            Assert.Equal("SHOP-CIPHER-1", query["shop_cipher"]);
            Assert.Equal("K", query["app_key"]);
            Assert.False(string.IsNullOrWhiteSpace(query["sign"]));
            Assert.False(string.IsNullOrWhiteSpace(query["timestamp"]));

            // Auth header present (value never asserted/printed beyond existence).
            Assert.True(request.Headers.Contains("x-tts-access-token"));
        }
    }

    // ------------------------------------------------------------------
    // latest_available_date, missing days, half-open boundaries
    // ------------------------------------------------------------------

    [Fact]
    public async Task IntervalAfterLatestAvailableDate_IsSkipped()
    {
        // latest_available_date=2026-10-19 but an extra (not-yet-final) 10-20
        // interval sneaks in: it must be dropped, not shown as a changing number.
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-19",
                "performance": {
                  "intervals": [
                    { "start_date": "2026-10-19", "end_date": "2026-10-20", "video_views": 23881 },
                    { "start_date": "2026-10-20", "end_date": "2026-10-21", "video_views": 99999 }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            var record = Assert.Single(result.Dataset!.Records);
            Assert.Equal(new DateTime(2026, 10, 19), record.Date);
            Assert.Equal(23881, record.VideoViews);
        }
    }

    [Fact]
    public async Task HalfOpenWindow_ExcludesDatesBeforeStartAndAtEndExclusive()
    {
        // Requested [2026-10-14, 2026-10-17): only 14, 15, 16 are in-window.
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-21",
                "performance": {
                  "intervals": [
                    { "start_date": "2026-10-13", "end_date": "2026-10-14", "video_views": 111 },
                    { "start_date": "2026-10-14", "end_date": "2026-10-15", "video_views": 222 },
                    { "start_date": "2026-10-15", "end_date": "2026-10-16", "video_views": 333 },
                    { "start_date": "2026-10-16", "end_date": "2026-10-17", "video_views": 444 },
                    { "start_date": "2026-10-17", "end_date": "2026-10-18", "video_views": 555 }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 17));

            Assert.True(result.IsAvailable);
            var records = result.Dataset!.Records;
            Assert.Equal(3, records.Count);
            Assert.Equal(new DateTime(2026, 10, 14), records[0].Date);
            Assert.Equal(222, records[0].VideoViews);
            Assert.Equal(new DateTime(2026, 10, 16), records[2].Date);
            Assert.Equal(444, records[2].VideoViews);
        }
    }

    [Fact]
    public async Task MissingDay_HasNoRecord_AndServiceRendersItNotNullZero()
    {
        // Verified real-world shape: interval for 2026-10-20 is simply absent.
        var (provider, _, db) = CreateProvider(VerifiedResponseJson);
        using (db)
        {
            var service = new DailyPerformanceService(provider, new FakeShopTimeZone());
            var data = await service.BuildAsync(new DailyReportFilter
            {
                StartDate = new DateTime(2026, 10, 14),
                EndDate = new DateTime(2026, 10, 20) // 7 days; only 6 have intervals
            });

            var videoViews = data.Cards.Single(c => c.Key == DailyReportMetrics.VideoViews);
            Assert.Equal(185855, videoViews.CurrentValue);
            Assert.Equal(DailyMetricStatus.Partial, videoViews.CurrentStatus);
            Assert.Contains("6 dari 7", videoViews.CoverageNote);

            var missingDay = data.Trend.Single(r => r.Date == new DateTime(2026, 10, 20));
            Assert.False(missingDay.HasRecord);
            Assert.Null(missingDay.VideoViews); // NOT 0

            var coveredDay = data.Trend.Single(r => r.Date == new DateTime(2026, 10, 14));
            Assert.True(coveredDay.HasRecord);
            Assert.Equal(38771, coveredDay.VideoViews);

            Assert.True(data.HasAnyMetricData);
        }
    }

    [Fact]
    public async Task LegitimateZero_IsKeptAsZero()
    {
        // A returned video_views of 0 is a real measurement, not missing data.
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-15",
                "performance": {
                  "intervals": [
                    { "start_date": "2026-10-14", "end_date": "2026-10-15", "video_views": 0 },
                    { "start_date": "2026-10-15", "end_date": "2026-10-16", "video_views": 5 }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var service = new DailyPerformanceService(provider, new FakeShopTimeZone());
            var data = await service.BuildAsync(new DailyReportFilter
            {
                StartDate = new DateTime(2026, 10, 14),
                EndDate = new DateTime(2026, 10, 15)
            });

            var zeroDay = data.Trend.Single(r => r.Date == new DateTime(2026, 10, 14));
            Assert.True(zeroDay.HasRecord);
            Assert.Equal(0, zeroDay.VideoViews); // zero stays zero

            var videoViews = data.Cards.Single(c => c.Key == DailyReportMetrics.VideoViews);
            Assert.Equal(5, videoViews.CurrentValue);
            Assert.Equal(DailyMetricStatus.Complete, videoViews.CurrentStatus); // both days covered
        }
    }

    [Fact]
    public async Task IntervalWithoutVideoViewsField_YieldsNullNotZero()
    {
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-14",
                "performance": {
                  "intervals": [
                    { "start_date": "2026-10-14", "end_date": "2026-10-15", "product_impressions": 438 }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            var record = Assert.Single(result.Dataset!.Records);
            Assert.Null(record.VideoViews); // field absent -> no value, never 0
        }
    }

    [Fact]
    public async Task MultiDayInterval_IsNotAttributedToSingleDay()
    {
        // A non-1D interval cannot be honestly split into days - it is skipped.
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-16",
                "performance": {
                  "intervals": [
                    { "start_date": "2026-10-14", "end_date": "2026-10-16", "video_views": 100 }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            Assert.Empty(result.Dataset!.Records);
        }
    }

    // ------------------------------------------------------------------
    // Unrelated fields stay unmapped (verified scope only)
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnrelatedFields_AreNeverMapped_ToDailyReportMetrics()
    {
        var (provider, _, db) = CreateProvider(ResponseWithUnrelatedFieldsJson);
        using (db)
        {
            var service = new DailyPerformanceService(provider, new FakeShopTimeZone());
            var data = await service.BuildAsync(new DailyReportFilter
            {
                StartDate = new DateTime(2026, 10, 14),
                EndDate = new DateTime(2026, 10, 14)
            });

            // Video Views is the verified metric: real value from the fixture.
            var videoViews = data.Cards.Single(c => c.Key == DailyReportMetrics.VideoViews);
            Assert.Equal(38771, videoViews.CurrentValue);

            // Everything else is Unavailable ("-"), NOT populated from
            // video_product_viewers, traffic.*, sales.* or any other field.
            foreach (var key in new[]
            {
                DailyReportMetrics.Reach,
                DailyReportMetrics.ProfileViews,
                DailyReportMetrics.Interactions,
                DailyReportMetrics.NetFollower,
                DailyReportMetrics.TotalFollower
            })
            {
                var card = data.Cards.Single(c => c.Key == key);
                Assert.Null(card.CurrentValue);
                Assert.Equal(DailyMetricStatus.Unavailable, card.CurrentStatus);
                Assert.Contains("terverifikasi", card.UnavailableReason);
            }

            var engagementRate = data.Cards.Single(c => c.Key == DailyReportMetrics.EngagementRate);
            Assert.Null(engagementRate.CurrentValue);
            Assert.Equal(DailyMetricStatus.Unavailable, engagementRate.CurrentStatus);

            var row = Assert.Single(data.Trend);
            Assert.Equal(38771, row.VideoViews);
            Assert.Null(row.Reach);
            Assert.Null(row.ProfileViews);
            Assert.Null(row.Interactions);
            Assert.Null(row.NetFollower);
            Assert.Null(row.EngagementRate);

            Assert.False(data.InteractionComposition.IsAvailable);
            Assert.False(data.ViewerComposition.IsAvailable);
            Assert.All(data.ViewerComposition.Segments, s => Assert.Null(s.Value));

            // The page explains the partial scope instead of hiding it.
            Assert.Contains(data.Notices, n => n.Contains("belum memiliki sumber data yang terverifikasi"));

            // Footer notes distinguish verified vs unverified per metric.
            Assert.Contains(data.SourceNotes, n => n.Metric == "Video Views" && n.Status.Contains("tersedia dari"));
            Assert.Contains(data.SourceNotes, n => n.Metric == "Reach" && n.Status == "belum tersedia dari sumber yang terverifikasi");
        }
    }

    // ------------------------------------------------------------------
    // Empty payload, malformed payload, API errors, auth failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task EmptyIntervals_Available_ButNoRecords()
    {
        var (provider, _, db) = CreateProvider(EmptyIntervalsJson);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            Assert.Empty(result.Dataset!.Records);
        }
    }

    [Fact]
    public async Task MalformedJson_ReturnsUnavailable_WithReason()
    {
        var (provider, _, db) = CreateProvider("this is not json {");
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.False(result.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(result.UnavailableReason));
            Assert.Null(result.Dataset);
        }
    }

    [Fact]
    public async Task BusinessError_ReturnsUnavailable_WithCodeAndRequestId()
    {
        var (provider, _, db) = CreateProvider(BusinessErrorJson);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.False(result.IsAvailable);
            Assert.Contains("36009002", result.UnavailableReason);
            Assert.Contains("rate limit exceeded", result.UnavailableReason);
            Assert.Contains("req-36009002", result.UnavailableReason);
            Assert.Null(result.Dataset);
        }
    }

    [Fact]
    public async Task HttpError_ReturnsUnavailable_NotAnException()
    {
        var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream error", Encoding.UTF8, "text/plain")
        });
        var factory = new SimpleHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
        });
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
        db.TikTokShops.Add(new TikTokShop { ShopCipher = "SHOP-CIPHER-1" });
        db.SaveChanges();

        var options = Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" });
        var provider = new TikTokShopVideoOverviewMetricSource(
            factory, options, db, new TikTokSignatureService(options), new StubAuth("TOKEN"));

        var result = await provider.GetAsync(Window(10, 14, 10, 21));

        Assert.False(result.IsAvailable);
        Assert.Contains("500", result.UnavailableReason);
        Assert.Null(result.Dataset);
    }

    [Fact]
    public async Task AuthFailure_NoToken_ReturnsUnavailable_WithoutHttpCall()
    {
        var (provider, handler, db) = CreateProvider(VerifiedResponseJson, accessToken: null);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.False(result.IsAvailable);
            Assert.Contains("token", result.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(handler.Requests); // no network attempt without a token
        }
    }

    [Fact]
    public async Task MissingCredential_ReturnsUnavailable_WithoutHttpCall()
    {
        var (provider, handler, db) = CreateProvider(VerifiedResponseJson, seedCredential: false);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.False(result.IsAvailable);
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task MissingShop_ReturnsUnavailable_WithoutHttpCall()
    {
        var (provider, handler, db) = CreateProvider(VerifiedResponseJson, seedShop: false);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.False(result.IsAvailable);
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task MissingAppKeyConfiguration_ReturnsUnavailable_WithoutTouchingDb()
    {
        var handler = new ScriptedHandler(() => OkJson(VerifiedResponseJson));
        var factory = new SimpleHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
        });
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var options = Options.Create(new TikTokOptions()); // no AppKey/AppSecret
        var provider = new TikTokShopVideoOverviewMetricSource(
            factory, options, db, new TikTokSignatureService(options), new StubAuth("TOKEN"));

        var result = await provider.GetAsync(Window(10, 14, 10, 21));

        Assert.False(result.IsAvailable);
        Assert.Contains("AppKey", result.UnavailableReason);
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // String-encoded integers (TikTok returns both shapes)
    // ------------------------------------------------------------------

    [Fact]
    public async Task StringEncodedVideoViews_AreParsed()
    {
        const string json = """
            {
              "code": "0",
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-14",
                "performance": {
                  "intervals": [
                    { "start_date": "2026-10-14", "end_date": "2026-10-15", "video_views": "38771" }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            var record = Assert.Single(result.Dataset!.Records);
            Assert.Equal(38771, record.VideoViews);
        }
    }

    // ------------------------------------------------------------------
    // Data-integrity guard: unreachable records never fabricate values
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordsOutsideRequestedWindow_AreIgnored()
    {
        // A stray interval outside the requested window must not leak into the report.
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-21",
                "performance": {
                  "intervals": [
                    { "start_date": "2026-09-01", "end_date": "2026-09-02", "video_views": 123 },
                    { "start_date": "2026-10-14", "end_date": "2026-10-15", "video_views": 456 }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            var record = Assert.Single(result.Dataset!.Records);
            Assert.Equal(456, record.VideoViews);
        }
    }

    // ------------------------------------------------------------------
    // Verified mapping: TikTok shop video performance extra record fields
    // (READ-ONLY daily metadata - NOT dashboard summary metrics)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExtraRecordFields_AreMapped_FromVerifiedResponseShape()
    {
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "request_id": "req-extra-0001",
              "data": {
                "latest_available_date": "2026-10-19",
                "performance": {
                  "intervals": [
                    {
                      "start_date": "2026-10-14",
                      "end_date": "2026-10-15",
                      "video_views": 1000,
                      "product_impressions": 438,
                      "product_clicks": 22,
                      "video_attributed_sku_orders": 3,
                      "video_attributed_gmv": { "amount": "1500.00", "currency": "IDR" },
                      "shoppable_video_attributed_gmv": { "amount": "1200.00", "currency": "IDR" },
                      "video_gmv": { "amount": "5000.50", "currency": "IDR" },
                      "video_indirect_gmv": { "amount": "750.25", "currency": "USD" },
                      "ctr": "0.0545",
                      "ctor_sku_order": 0.13636,
                      "gpm": { "amount": "500.00", "currency": "IDR" },
                      "avg_daily_customers": "1.5",
                      "video_product_viewers": 99
                    }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            var record = Assert.Single(result.Dataset!.Records);

            Assert.Equal(1000, record.VideoViews);
            Assert.Equal(438, record.ProductImpressions);
            Assert.Equal(22, record.ProductClicks);
            Assert.Equal(3, record.VideoAttributedSkuOrders);
            Assert.Equal(1500.00m, record.VideoAttributedGmvAmount);
            Assert.Equal("IDR", record.VideoAttributedGmvCurrency);
            Assert.Equal(1200.00m, record.ShoppableVideoAttributedGmvAmount);
            Assert.Equal("IDR", record.ShoppableVideoAttributedGmvCurrency);
            Assert.Equal(5000.50m, record.VideoGmvAmount);
            Assert.Equal("IDR", record.VideoGmvCurrency);
            Assert.Equal(750.25m, record.VideoIndirectGmvAmount);
            Assert.Equal("USD", record.VideoIndirectGmvCurrency);
            Assert.Equal("IDR", record.GpmCurrency);
            Assert.Equal(500.00m, record.GpmAmount);
            Assert.Equal(0.0545m, record.Ctr);
            Assert.Equal(0.13636m, record.CtorSkuOrder);
            Assert.Equal(1.5m, record.AvgDailyCustomers);
            Assert.Equal(500.00m, record.GpmAmount);
            Assert.Equal("IDR", record.GpmCurrency);
            Assert.Equal(99, record.VideoProductViewers);
        }
    }

    [Fact]
    public async Task ExtraRecordFields_AcceptStringEncodedDecimals()
    {
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-14",
                "performance": {
                  "intervals": [
                    {
                      "start_date": "2026-10-14",
                      "end_date": "2026-10-15",
                      "video_views": "38771",
                      "product_impressions": "438",
                      "product_clicks": "22",
                      "video_attributed_sku_orders": "3",
                      "video_attributed_gmv": { "amount": "1500.00", "currency": "IDR" },
                      "shoppable_video_attributed_gmv": { "amount": "0.00", "currency": "0" },
                      "video_gmv": { "amount": "5000.50" },
                      "video_indirect_gmv": { "amount": "750.25", "currency": "USD" },
                      "ctr": "0.0545",
                      "ctor_sku_order": "0.13636",
                      "gpm": { "amount": "500.00", "currency": "IDR" },
                      "avg_daily_customers": "1.5",
                      "video_product_viewers": "99"
                    }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            var record = Assert.Single(result.Dataset!.Records);

            Assert.Equal(38771, record.VideoViews);
            Assert.Equal(438, record.ProductImpressions);
            Assert.Equal(22, record.ProductClicks);
            Assert.Equal(3, record.VideoAttributedSkuOrders);
            Assert.Equal(1500.00m, record.VideoAttributedGmvAmount);
            Assert.Equal("IDR", record.VideoAttributedGmvCurrency);
            Assert.Equal("IDR", record.GpmCurrency);
            Assert.Equal("IDR", record.GpmCurrency);

            // gpm.amount is present on the same object.
            Assert.Equal(500.00m, record.GpmAmount);
            Assert.Equal("IDR", record.GpmCurrency);

            // shoppable_video_attributed_gmv.amount present, but the currency string
            // "0" is not a real currency code, so it is treated as absent and stored as
            // null. The amount is still parsed correctly.
            Assert.Equal(0.00m, record.ShoppableVideoAttributedGmvAmount);
            Assert.Null(record.ShoppableVideoAttributedGmvCurrency);
            Assert.Equal("IDR", record.VideoAttributedGmvCurrency);

            // video_gmv.amount present, currency absent.
            Assert.Equal(5000.50m, record.VideoGmvAmount);
            Assert.Null(record.VideoGmvCurrency);

            Assert.Equal(750.25m, record.VideoIndirectGmvAmount);
            Assert.Equal("USD", record.VideoIndirectGmvCurrency);
            Assert.Equal(0.0545m, record.Ctr);
            Assert.Equal(0.13636m, record.CtorSkuOrder);
            Assert.Equal(1.5m, record.AvgDailyCustomers);
            Assert.Equal(99, record.VideoProductViewers);
        }
    }

    [Fact]
    public async Task ExtraRecordFields_AbsentFieldsStayNull_NeverZero()
    {
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-14",
                "performance": {
                  "intervals": [
                    {
                      "start_date": "2026-10-14",
                      "end_date": "2026-10-15",
                      "video_views": 100
                    }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            var record = Assert.Single(result.Dataset!.Records);

            Assert.Equal(100, record.VideoViews);
            Assert.Null(record.ProductImpressions);
            Assert.Null(record.ProductClicks);
            Assert.Null(record.VideoAttributedSkuOrders);
            Assert.Null(record.VideoAttributedGmvAmount);
            Assert.Null(record.VideoAttributedGmvCurrency);
            Assert.Null(record.ShoppableVideoAttributedGmvAmount);
            Assert.Null(record.ShoppableVideoAttributedGmvCurrency);
            Assert.Null(record.VideoGmvAmount);
            Assert.Null(record.VideoGmvCurrency);
            Assert.Null(record.VideoIndirectGmvAmount);
            Assert.Null(record.VideoIndirectGmvCurrency);
            Assert.Null(record.Ctr);
            Assert.Null(record.CtorSkuOrder);
            Assert.Null(record.GpmAmount);
            Assert.Null(record.GpmCurrency);
            Assert.Null(record.AvgDailyCustomers);
            Assert.Null(record.VideoProductViewers);
        }
    }

    [Fact]
    public async Task VideoProductViewers_IsNeverUsedAsReach()
    {
        // Even when video_product_viewers is present, Reach must stay null because
        // this endpoint does not verify an account-level Reach / Total Viewers field.
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-19",
                "performance": {
                  "intervals": [
                    {
                      "start_date": "2026-10-14",
                      "end_date": "2026-10-15",
                      "video_views": 38771,
                      "video_product_viewers": 99999
                    }
                  ]
                }
              }
            }
            """;

        var (provider, handler, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));
            var record = Assert.Single(result.Dataset!.Records);
            Assert.Equal(99999, record.VideoProductViewers);

            var service = new DailyPerformanceService(provider, new FakeShopTimeZone());
            var data = await service.BuildAsync(new DailyReportFilter
            {
                StartDate = new DateTime(2026, 10, 14),
                EndDate = new DateTime(2026, 10, 14)
            });

            // video_product_viewers is stored on the record but it is NOT mapped to
            // the Reach dashboard card, NOT mapped to Reach on the daily row, and
            // NOT used as the interaction-composition denominator.
            Assert.Equal(99999, record.VideoProductViewers);

            var reach = data.Cards.Single(c => c.Key == DailyReportMetrics.Reach);
            Assert.Null(reach.CurrentValue);
            Assert.Equal(DailyMetricStatus.Unavailable, reach.CurrentStatus);
            Assert.Contains("terverifikasi", reach.UnavailableReason);

            var row = data.Trend.Single();
            Assert.Equal(38771, row.VideoViews);
            Assert.Null(row.Reach);
            Assert.Null(row.Interactions);
            Assert.Null(row.EngagementRate);

            Assert.False(data.InteractionComposition.IsAvailable);
        }
    }

    [Fact]
    public async Task DeclaredMetricKeys_AddsTheVerifiedRecordFields_NotDashboardMetricKeys()
    {
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-14",
                "performance": {
                  "intervals": [
                    {
                      "start_date": "2026-10-14",
                      "end_date": "2026-10-15",
                      "video_views": 100
                    }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            var keys = result.Dataset!.AvailableMetricKeys!.OrderBy(k => k).ToList();

            // The only DASHBOARD summary metric declared is VideoViews.
            Assert.Contains(DailyReportMetrics.VideoViews, keys);
            Assert.DoesNotContain(DailyReportMetrics.Reach, keys);
            Assert.DoesNotContain(DailyReportMetrics.ProfileViews, keys);
            Assert.DoesNotContain(DailyReportMetrics.Interactions, keys);
            Assert.DoesNotContain(DailyReportMetrics.EngagementRate, keys);
            Assert.DoesNotContain(DailyReportMetrics.NetFollower, keys);
            Assert.DoesNotContain(DailyReportMetrics.TotalFollower, keys);

            // Verified record-level fields are declared so callers can discover what
            // this source actually populates without parsing the response shape.
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.VideoViews, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.ProductImpressions, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.ProductClicks, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.VideoAttributedSkuOrders, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.VideoAttributedGmv, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.ShoppableVideoAttributedGmv, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.VideoGmv, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.VideoIndirectGmv, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.Ctr, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.CtorSkuOrder, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.Gpm, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.AvgDailyCustomers, keys);
            Assert.Contains(TikTokShopVideoOverviewMetricSourceMetrics.VideoProductViewers, keys);
        }
    }

    [Fact]
    public async Task ExtraRecordFields_AreOnTheRecord_ButNotOnTheRowByDesign()
    {
        // The extra record fields live on AccountDailyMetricRecord (verified mapping),
        // but the Daily Report daily row intentionally does not copy them because the
        // row is for the seven dashboard columns only. This test documents the current
        // boundary so a future change is deliberate, not accidental.
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-19",
                "performance": {
                  "intervals": [
                    {
                      "start_date": "2026-10-14",
                      "end_date": "2026-10-15",
                      "video_views": 38771,
                      "video_product_viewers": 99999,
                      "ctr": "0.0545"
                    }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));
            var record = Assert.Single(result.Dataset!.Records);

            Assert.Equal(38771, record.VideoViews);
            Assert.Equal(99999, record.VideoProductViewers);
            Assert.Equal(0.0545m, record.Ctr);

            // The daily row stays focused on the dashboard columns.
            var service = new DailyPerformanceService(provider, new FakeShopTimeZone());
            var data = await service.BuildAsync(new DailyReportFilter
            {
                StartDate = new DateTime(2026, 10, 14),
                EndDate = new DateTime(2026, 10, 14)
            });

            var row = Assert.Single(data.Trend);
            Assert.Equal(38771, row.VideoViews);
            Assert.Null(row.Reach);
            Assert.Null(row.ProfileViews);
            Assert.Null(row.Interactions);
            Assert.Null(row.EngagementRate);
            Assert.Null(row.NetFollower);
        }
    }

    // ------------------------------------------------------------------
    // High-level service integration: extra record fields flow into the record,
    // but they MUST NOT leak into the seven dashboard summary cards.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExtraRecordFields_DoNotAffectSummaryCards_WhenSourceIsVerifiedPartial()
    {
        // Same verified fixture shape, but the interval also carries the full set of
        // TikTok shop video extra fields. Even with all of them present, the seven
        // dashboard cards must still render only Video Views as available.
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-19",
                "performance": {
                  "intervals": [
                    {
                      "start_date": "2026-10-14",
                      "end_date": "2026-10-15",
                      "video_views": 38771,
                      "product_impressions": 438,
                      "product_clicks": 22,
                      "video_attributed_sku_orders": 3,
                      "video_attributed_gmv": { "amount": "1500.00", "currency": "IDR" },
                      "shoppable_video_attributed_gmv": { "amount": "1200.00", "currency": "IDR" },
                      "video_gmv": { "amount": "5000.50", "currency": "IDR" },
                      "video_indirect_gmv": { "amount": "750.25", "currency": "USD" },
                      "ctr": "0.0545",
                      "ctor_sku_order": 0.13636,
                      "gpm": { "amount": "500.00", "currency": "IDR" },
                      "avg_daily_customers": "1.5",
                      "video_product_viewers": 99999
                    }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var service = new DailyPerformanceService(provider, new FakeShopTimeZone());
            var data = await service.BuildAsync(new DailyReportFilter
            {
                StartDate = new DateTime(2026, 10, 14),
                EndDate = new DateTime(2026, 10, 14)
            });

            // The verified dashboard metric for this endpoint is Video Views only.
            var videoViews = data.Cards.Single(c => c.Key == DailyReportMetrics.VideoViews);
            Assert.Equal(38771, videoViews.CurrentValue);
            Assert.Equal(DailyMetricStatus.Complete, videoViews.CurrentStatus);

            // Every other dashboard card must stay Unavailable, even though the record
            // itself carries real commerce/funnel numbers.
            foreach (var key in new[]
            {
                DailyReportMetrics.Reach,
                DailyReportMetrics.ProfileViews,
                DailyReportMetrics.Interactions,
                DailyReportMetrics.EngagementRate,
                DailyReportMetrics.NetFollower,
                DailyReportMetrics.TotalFollower
            })
            {
                var card = data.Cards.Single(c => c.Key == key);
                Assert.Null(card.CurrentValue);
                Assert.Equal(DailyMetricStatus.Unavailable, card.CurrentStatus);
                Assert.Contains("terverifikasi", card.UnavailableReason);
            }

            // Daily rows must not leak commerce fields into Reach/Interactions/followers.
            var row = Assert.Single(data.Trend);
            Assert.Equal(38771, row.VideoViews);
            Assert.Null(row.Reach);
            Assert.Null(row.ProfileViews);
            Assert.Null(row.Interactions);
            Assert.Null(row.EngagementRate);
            Assert.Null(row.NetFollower);

            Assert.False(data.InteractionComposition.IsAvailable);
            Assert.False(data.ViewerComposition.IsAvailable);
        }
    }

    [Fact]
    public async Task ExtraRecordFields_PerDayMetadata_StaysPerDay_NotAggregated()
    {
        // Two days, each with its own commerce/funnel metadata. The summary cards must
        // not sum/average these read-only fields because they are NOT dashboard summary
        // metrics. This test exists to guard against a future regression that would e.g.
        // treat ctr as a summable quantity or gpm amounts as a summable GMV figure.
        const string json = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "latest_available_date": "2026-10-19",
                "performance": {
                  "intervals": [
                    {
                      "start_date": "2026-10-14",
                      "end_date": "2026-10-15",
                      "video_views": 100,
                      "ctr": "0.10",
                      "gpm": { "amount": "100.00", "currency": "IDR" }
                    },
                    {
                      "start_date": "2026-10-15",
                      "end_date": "2026-10-16",
                      "video_views": 200,
                      "ctr": "0.20",
                      "gpm": { "amount": "200.00", "currency": "IDR" }
                    }
                  ]
                }
              }
            }
            """;

        var (provider, _, db) = CreateProvider(json);
        using (db)
        {
            var service = new DailyPerformanceService(provider, new FakeShopTimeZone());
            var data = await service.BuildAsync(new DailyReportFilter
            {
                StartDate = new DateTime(2026, 10, 14),
                EndDate = new DateTime(2026, 10, 15)
            });

            // Video Views is the only dashboard metric mapped here, and it still sums
            // correctly per day (because that is the verified dashboard metric).
            var videoViews = data.Cards.Single(c => c.Key == DailyReportMetrics.VideoViews);
            Assert.Equal(300, videoViews.CurrentValue);
            Assert.Equal(DailyMetricStatus.Complete, videoViews.CurrentStatus);

            // The extra fields remain per-day metadata on the record; they are NOT
            // summed/averaged into any dashboard card. The public dashboard output is
            // the contract that must stay correct even if the internal record shape
            // changes, so this test asserts against Cards/Trend rather than the raw
            // records. The record-level values are validated directly in the mapping
            // tests above.
            Assert.True(data.Cards.All(c => c.Key == DailyReportMetrics.VideoViews
                ? c.CurrentValue == 300m
                : !c.IsAvailable));

            var row1 = data.Trend.Single(r => r.Date == new DateTime(2026, 10, 14));
            var row2 = data.Trend.Single(r => r.Date == new DateTime(2026, 10, 15));
            Assert.Equal(100, row1.VideoViews);
            Assert.Equal(200, row2.VideoViews);
            Assert.Null(row1.Reach);
            Assert.Null(row2.Reach);
            Assert.Null(row1.Interactions);
            Assert.Null(row2.Interactions);
            Assert.Null(row1.EngagementRate);
            Assert.Null(row2.EngagementRate);
        }
    }

    // ------------------------------------------------------------------
    // Bounded TRANSPORT retry (live-verified 2026-10-21: the shop network
    // intermittently times out at TCP-connect level; the identical signed
    // request succeeded seconds before and after one such failure)
    // ------------------------------------------------------------------

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond;

        public SequencedHandler(Func<int, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call;
            lock (Requests)
            {
                Requests.Add(request);
                call = Requests.Count;
            }

            return Task.FromResult(_respond(call));
        }
    }

    private static (TikTokShopVideoOverviewMetricSource Provider, SequencedHandler Handler, AppDbContext Db) CreateProviderWithHandler(
        SequencedHandler handler)
    {
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
            factory, options, db, new TikTokSignatureService(options), new StubAuth("TEST-ACCESS-TOKEN"));

        return (provider, handler, db);
    }

    [Fact]
    public async Task TransportFailureOnce_IsRetried_WithFreshSignedRequest_AndSucceeds()
    {
        var handler = new SequencedHandler(call => call == 1
            ? throw new HttpRequestException("connect timed out (simulated)")
            : OkJson(VerifiedResponseJson));

        var (provider, _, db) = CreateProviderWithHandler(handler);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            Assert.Equal(6, result.Dataset!.Records.Count);
            Assert.Equal(2, handler.Requests.Count);

            // Every attempt is a fully valid, independently signed request.
            foreach (var request in handler.Requests)
            {
                Assert.Equal("/analytics/202609/shop_videos/overview_performance", request.RequestUri!.AbsolutePath);
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                Assert.Equal("2026-10-14", query["start_date_ge"]);
                Assert.Equal("2026-10-21", query["end_date_lt"]);
                Assert.False(string.IsNullOrWhiteSpace(query["sign"]));
            }
        }
    }

    [Fact]
    public async Task TransportTimeoutOnce_IsRetried_AndSucceeds()
    {
        // HttpClient timeouts surface as TaskCanceledException with the caller's
        // token NOT cancelled - they must be retried, never propagated.
        var handler = new SequencedHandler(call => call == 1
            ? throw new TaskCanceledException("simulated send timeout")
            : OkJson(VerifiedResponseJson));

        var (provider, _, db) = CreateProviderWithHandler(handler);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.True(result.IsAvailable);
            Assert.Equal(6, result.Dataset!.Records.Count);
            Assert.Equal(2, handler.Requests.Count);
        }
    }

    [Fact]
    public async Task TransportFailurePersistent_ReturnsUnavailable_AfterBoundedAttempts()
    {
        var handler = new SequencedHandler(_ => throw new HttpRequestException("connect timed out (simulated)"));

        var (provider, _, db) = CreateProviderWithHandler(handler);
        using (db)
        {
            var result = await provider.GetAsync(Window(10, 14, 10, 21));

            Assert.False(result.IsAvailable);
            Assert.Contains("jaringan/transport", result.UnavailableReason);
            // Bounded: the retry never loops forever and never fabricates data.
            Assert.Equal(TikTokShopVideoOverviewMetricSource.MaxTransportAttempts, handler.Requests.Count);
        }
    }
}
