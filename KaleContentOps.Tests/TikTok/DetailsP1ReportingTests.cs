using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
using KaleContentOps.Tests;
using Xunit;

namespace KaleContentOps.TikTok.Tests
{
    public class DetailsP1ReportingTests
    {
        private const string RealWireResponseJson = """
            {
              "code": 0,
              "message": "OK",
              "data": {
                "performance": {
                  "intervals": [ { "traffic": { "comments": 0, "likes": 13, "new_followers": 0, "shares": 0, "views": 263 } } ],
                  "viewer_profile": []
                }
              }
            }
            """;

        private const string RateLimit36009002Json = "{\"code\":36009002,\"message\":\"rate limit exceeded\",\"request_id\":\"req-36009002\"}";

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
                Content = new StringContent("{\"code\":0,\"message\":\"too many requests\"}", Encoding.UTF8, "application/json")
            };
            res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return res;
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

        private static async Task<TikTokShop> SeedShopAsync(AppDbContext db, string shopCipher)
        {
            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new TikTokShop { ShopCipher = shopCipher };
            db.TikTokShops.Add(shop);
            await db.SaveChangesAsync();
            return shop;
        }

        private static async Task<ContentLog> SeedShopLogWithMetricAsync(AppDbContext db, string shopCipher, string videoId, bool viewsOnly = true)
        {
            var shop = await db.TikTokShops.SingleAsync(s => s.ShopCipher == shopCipher);
            var log = new ContentLog { VideoId = videoId, TikTokShop = shop };
            db.ContentLogs.Add(log);
            await db.SaveChangesAsync();
            // add a "latest" metric that is views-only (un-enriched) so it becomes a P1 candidate
            var metric = new ContentMetric { ContentLogId = log.Id, CapturedAt = DateTime.UtcNow, Views = 1 };
            db.ContentMetrics.Add(metric);
            await db.SaveChangesAsync();
            return log;
        }

        [Fact]
        public async Task P1_ZeroCandidates_CandidatesSelected0_Attempted0()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var shop = await SeedShopAsync(db, "SHOP-ZERO");

            var handler = new ScriptedHandler(attempt => OkJson(RealWireResponseJson));
            var svc = CreateDetailsSyncService(db, handler);

            var res = await svc.RunP1DetailsSyncAsync(shop.ShopCipher, limit: 10);

            Assert.NotNull(res);
            Assert.Equal(0, res.CandidatesSelected);
            Assert.Equal(0, res.Attempted);
        }

        [Fact]
        public async Task TenCandidates_AttemptedReflectsActualPerVideoAttempts_And_NewContentMetrics()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var shop = await SeedShopAsync(db, "SHOP-10");

            // create 10 P1 candidates
            for (int i = 0; i < 10; i++) await SeedShopLogWithMetricAsync(db, shop.ShopCipher, $"VID-{i}");

            var handler = new ScriptedHandler(attempt => OkJson(RealWireResponseJson));
            var svc = CreateDetailsSyncService(db, handler);

            var res = await svc.RunP1DetailsSyncAsync(shop.ShopCipher, limit: 10);

            Assert.NotNull(res);
            Assert.Equal(10, res.CandidatesSelected);
            Assert.Equal(10, res.Attempted);
            Assert.Equal(10, res.NewContentMetrics);
            Assert.Equal(10, res.Succeeded);
        }

        [Fact]
        public async Task Throttle_OnHttp429_StopsBatch_And_ThrottledIncremented()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var shop = await SeedShopAsync(db, "SHOP-429");
            for (int i = 0; i < 5; i++) await SeedShopLogWithMetricAsync(db, shop.ShopCipher, $"VID-{i}");

            // Always rate-limited to simulate persistent HTTP 429 (service will eventually give up and return 429)
            var handler = new ScriptedHandler(attempt => RateLimited());
            var svc = CreateDetailsSyncService(db, handler);

            var res = await svc.RunP1DetailsSyncAsync(shop.ShopCipher, limit: 5);

            Assert.NotNull(res);
            Assert.Equal(1, res.Throttled);
            Assert.Equal(1, res.Attempted);
        }

        [Fact]
        public async Task BusinessCode_36009002_Is_Detected_And_Reported()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var shop = await SeedShopAsync(db, "SHOP-36009002");
            await SeedShopLogWithMetricAsync(db, shop.ShopCipher, "VID-1");

            var handler = new ScriptedHandler(attempt => OkJson(RateLimit36009002Json));
            var svc = CreateDetailsSyncService(db, handler);

            var res = await svc.RunP1DetailsSyncAsync(shop.ShopCipher, limit: 10);

            Assert.NotNull(res);
            Assert.Equal(1, res.RateLimit36009002);
        }
    }
}
