using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using Xunit;
using KaleContentOps.Tests;

namespace KaleContentOps.TikTok.Tests
{
    // Phase 1A (Data Foundation) tests for the Details Sync core metrics:
    // Reach, Likes, Comments, Shares, NewFollowers.
    //
    // Payload evidence: REAL verified response of
    // GET /analytics/202509/shop_videos/7687996887235890452/performance
    // (same wire response as DetailsSyncPersistenceE2ETests). The real payload carries
    // likes=13, views=263 and explicit zeros for comments/shares/new_followers, and has
    // NO reach field - so reach-zero preservation is proven at the parser level with the
    // same structure plus "reach": 0 (see Reach_Zero_Is_Preserved_As_Zero below).
    public class DetailsSyncPhase1ATests
    {
        private const string RealWireResponseJson = """
            {
              "code": 0,
              "message": "OK",
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "comments": 0,
                        "likes": 13,
                        "new_followers": 0,
                        "shares": 0,
                        "views": 263
                      }
                    }
                  ],
                  "viewer_profile": []
                }
              }
            }
            """;

        // Known TikTok shared app-group rate-limit business code (verified behavior).
        private const string RateLimit36009002Json = "{\"code\":36009002,\"message\":\"rate limit exceeded\",\"request_id\":\"req-36009002\"}";

        // Handler that scripts one response per request attempt and counts requests.
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<int, HttpResponseMessage> _respond;
            public int RequestCount;
            public ScriptedHandler(Func<int, HttpResponseMessage> respond) => _respond = respond;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var attempt = Interlocked.Increment(ref RequestCount);
                return Task.FromResult(_respond(attempt));
            }
        }

        private sealed class StubAuth : ITikTokAuthService
        {
            public Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
            public Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
            public Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
            public Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("TOKEN");
        }

        private static HttpResponseMessage OkJson(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage RateLimited()
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(RateLimit36009002Json, Encoding.UTF8, "application/json")
            };
            res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return res;
        }

        private static async Task<ContentLog> SeedShopAndLogAsync(AppDbContext db, string shopCipher, string videoId)
        {
            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new TikTokShop { ShopCipher = shopCipher };
            db.TikTokShops.Add(shop);
            var log = new ContentLog { VideoId = videoId, TikTokShop = shop };
            db.ContentLogs.Add(log);
            await db.SaveChangesAsync();
            return log;
        }

        private static TikTokDetailsSyncService CreateDetailsSyncService(AppDbContext db, ScriptedHandler handler)
        {
            var factory = new SimpleHttpClientFactory(new HttpClient(handler)
            {
                BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
            });
            var opts = Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" });
            var videoSvc = new TikTokVideoService(factory, opts, db, new StubAuth(), new TikTokSignatureService(opts));
            return new TikTokDetailsSyncService(
                factory,
                Options.Create(new TikTokOptions()),
                db,
                videoSvc,
                NullLogger<TikTokDetailsSyncService>.Instance);
        }

        [Fact]
        public async Task DetailsSync_Retries_Through_Http429_Then_Persists_Core_Metrics()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var log = await SeedShopAndLogAsync(db, "SHOP-P1A-429RETRY", "7687996887235890452");

            // First request: HTTP 429. Second request: the real verified payload.
            var handler = new ScriptedHandler(attempt => attempt == 1 ? RateLimited() : OkJson(RealWireResponseJson));
            var detailsSvc = CreateDetailsSyncService(db, handler);

            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP-P1A-429RETRY", limit: 10);

            Assert.Equal(2, handler.RequestCount); // one 429, then one success
            Assert.Equal(1, processed);

            var metric = await db.ContentMetrics.SingleAsync(m => m.ContentLogId == log.Id);
            Assert.Equal(263, metric.Views);
            Assert.Equal(13, metric.Likes);
            Assert.True(metric.Comments.HasValue); // valid zero, NOT null
            Assert.Equal(0, metric.Comments);
            Assert.True(metric.Shares.HasValue);
            Assert.Equal(0, metric.Shares);
            Assert.True(metric.NewFollowers.HasValue);
            Assert.Equal(0, metric.NewFollowers);
            Assert.Null(metric.Reach);             // real payload has no reach field -> NULL
            Assert.True(metric.CapturedAt > DateTime.UtcNow.AddMinutes(-5));
            Assert.True(metric.CapturedAt <= DateTime.UtcNow.AddMinutes(5));
        }

        [Fact]
        public async Task DetailsSync_Retries_Through_BusinessCode_36009002_Then_Persists()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var log = await SeedShopAndLogAsync(db, "SHOP-P1A-36009002", "7687996887235890452");

            // First request: HTTP 200 with business code 36009002 (rate limit).
            // Second request: the real verified payload.
            var handler = new ScriptedHandler(attempt => attempt == 1
                ? OkJson(RateLimit36009002Json)
                : OkJson(RealWireResponseJson));
            var detailsSvc = CreateDetailsSyncService(db, handler);

            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP-P1A-36009002", limit: 10);

            Assert.Equal(2, handler.RequestCount); // 36009002 was retried, not treated as data
            Assert.Equal(1, processed);

            var metric = await db.ContentMetrics.SingleAsync(m => m.ContentLogId == log.Id);
            Assert.Equal(13, metric.Likes);
            Assert.Equal(0, metric.Comments);
            Assert.Equal(0, metric.Shares);
            Assert.Equal(0, metric.NewFollowers);
            Assert.Null(metric.Reach);
            Assert.True(metric.CapturedAt > DateTime.UtcNow.AddMinutes(-5));
        }

        [Fact]
        public async Task DetailsSync_429_Exhaustion_Stops_Batch_And_Rerun_Is_Safe()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var log = await SeedShopAndLogAsync(db, "SHOP-P1A-429EXHAUST", "7687996887235890452");

            // Attempts 1-5: always 429 with Retry-After 1s (4 retry delays + final),
            // afterwards succeed (simulating the throttle clearing before the next run).
            var handler = new ScriptedHandler(attempt => attempt <= 5 ? RateLimited() : OkJson(RealWireResponseJson));
            var detailsSvc = CreateDetailsSyncService(db, handler);

            var processedRun1 = await detailsSvc.RunDetailsSyncAsync("SHOP-P1A-429EXHAUST", limit: 10);

            // Run 1: all 5 attempts rate-limited (4 retry + final), batch stops, nothing persisted.
            Assert.Equal(5, handler.RequestCount);
            Assert.Equal(0, processedRun1);
            Assert.False(await db.ContentMetrics.AnyAsync(m => m.ContentLogId == log.Id));

            // Immediate rerun: shop suspension is active -> fail-fast, still no writes.
            var processedRun2 = await detailsSvc.RunDetailsSyncAsync("SHOP-P1A-429EXHAUST", limit: 10);
            Assert.Equal(5, handler.RequestCount); // no new HTTP request while suspended
            Assert.Equal(0, processedRun2);
            Assert.False(await db.ContentMetrics.AnyAsync(m => m.ContentLogId == log.Id));

            // After the suspension window (Retry-After 1s) the rerun succeeds and the
            // metric is captured.
            await Task.Delay(1100);
            var processedRun3 = await detailsSvc.RunDetailsSyncAsync("SHOP-P1A-429EXHAUST", limit: 10);
            Assert.Equal(1, processedRun3);
            Assert.Equal(6, handler.RequestCount); // exactly one fresh HTTP request for run 3

            var metrics = await db.ContentMetrics.Where(m => m.ContentLogId == log.Id).ToListAsync();
            var metric = Assert.Single(metrics);
            Assert.Equal(263, metric.Views);
            Assert.Equal(13, metric.Likes);
            Assert.Equal(0, metric.Comments);
            Assert.Equal(0, metric.Shares);
            Assert.Equal(0, metric.NewFollowers);
        }

        [Fact]
        public async Task DetailsSync_Repeated_Runs_Append_Snapshots_And_Newest_Snapshot_Wins()
        {
            // Documents the ACTUAL persistence design: append-only snapshots (no idempotency
            // rule exists), read paths take the newest CapturedAt. Repeated execution is safe:
            // every snapshot carries all five core metrics and a CapturedAt, and the newest
            // row is what the Content Log surfaces.
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var log = await SeedShopAndLogAsync(db, "SHOP-P1A-IDEMPOTENT", "7687996887235890452");

            var handler = new ScriptedHandler(_ => OkJson(RealWireResponseJson));
            var detailsSvc = CreateDetailsSyncService(db, handler);

            var first = await detailsSvc.RunDetailsSyncAsync("SHOP-P1A-IDEMPOTENT", limit: 10);
            var second = await detailsSvc.RunDetailsSyncAsync("SHOP-P1A-IDEMPOTENT", limit: 10);

            Assert.Equal(1, first);
            Assert.Equal(1, second); // enriched logs stay eligible: a fresh snapshot is appended

            var metrics = await db.ContentMetrics.Where(m => m.ContentLogId == log.Id).ToListAsync();
            Assert.Equal(2, metrics.Count);
            Assert.All(metrics, m =>
            {
                Assert.Equal(263, m.Views);
                Assert.Equal(13, m.Likes);
                Assert.Equal(0, m.Comments);
                Assert.Equal(0, m.Shares);
                Assert.Equal(0, m.NewFollowers);
                Assert.True(m.CapturedAt > DateTime.UtcNow.AddMinutes(-5));
            });

            // Newest snapshot (what Content Log queries select) is the last one inserted.
            var latest = metrics.OrderByDescending(m => m.CapturedAt).ThenByDescending(m => m.Id).First();
            Assert.Equal(metrics.Max(m => m.Id), latest.Id);
        }

        [Fact]
        public void Reach_Zero_Is_Preserved_As_Zero()
        {
            // The verified real payload does NOT contain reach. This test uses the identical
            // structure plus "reach": 0 to prove the parser never turns a present zero into null.
            const string json = """
                {
                  "code": 0,
                  "data": {
                    "performance": {
                      "intervals": [
                        {
                          "traffic": {
                            "comments": 0,
                            "likes": 13,
                            "new_followers": 0,
                            "reach": 0,
                            "shares": 0,
                            "views": 263
                          }
                        }
                      ],
                      "viewer_profile": []
                    }
                  }
                }
                """;

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;
            var dm = svc.ParseDetailsMetrics(data);

            Assert.True(dm.Reach.HasValue);
            Assert.Equal(0, dm.Reach);
            Assert.Equal(13, dm.Likes);
            Assert.Equal(0, dm.Comments);
            Assert.Equal(0, dm.Shares);
            Assert.Equal(0, dm.NewFollowers);

            var metric = svc.MapDetailsMetricsToContentMetric(dm, contentLogId: 1);
            Assert.NotNull(metric);
            Assert.True(metric!.Reach.HasValue);
            Assert.Equal(0, metric.Reach);
        }
    }
}
