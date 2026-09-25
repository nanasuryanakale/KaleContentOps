using System;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    // End-to-end proof of "real Details response -> RunDetailsSyncAsync -> ContentMetrics rows".
    // HTTP layer is stubbed; the payload is the REAL response structure of
    // GET /analytics/202509/shop_videos/7687996887235890452/performance.
    // Uses the REAL TikTokVideoService because TikTokDetailsSyncService requires the
    // concrete class for ParseDetailsMetrics/MapDetailsMetricsToContentMetric.
    public class DetailsSyncPersistenceE2ETests
    {
        // Full wire response (RunDetailsDiagnosticAsync extracts data.performance from data)
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
                      },
                      "sales": {
                        "overall": {
                          "ctr": "0.0570",
                          "customers": 0,
                          "gmv": { "amount": "0.00", "currency": "IDR" },
                          "gpm": { "amount": "0.00", "currency": "IDR" },
                          "items_sold": 0,
                          "product_clicks": 15,
                          "product_impressions": 212
                        }
                      }
                    }
                  ],
                  "viewer_profile": []
                }
              }
            }
            """;

        private sealed class StaticHandler : HttpMessageHandler
        {
            private readonly string _json;
            public StaticHandler(string json) => _json = json;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_json, Encoding.UTF8, "application/json")
                });
        }

        private sealed class StubAuth : ITikTokAuthService
        {
            public Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
            public Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
            public Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
            public Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("TOKEN");
        }

        private static TikTokVideoService CreateRealVideoService(AppDbContext db, string wireResponseJson)
        {
            var factory = new SimpleHttpClientFactory(new HttpClient(new StaticHandler(wireResponseJson))
            {
                BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
            });
            var sig = new TikTokSignatureService(Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" }));
            return new TikTokVideoService(factory, Options.Create(new TikTokOptions { AppKey = "K" }), db, new StubAuth(), sig);
        }

        [Fact]
        public async Task RunDetailsSyncAsync_Persists_RealEngagement_Snapshot()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new KaleContentOps.Models.TikTokShop { ShopCipher = "SHOP1" };
            db.TikTokShops.Add(shop);
            db.ContentLogs.Add(new KaleContentOps.Models.ContentLog
            {
                VideoId = "7687996887235890452",
                TikTokShop = shop
            });
            await db.SaveChangesAsync();

            var videoSvc = CreateRealVideoService(db, RealWireResponseJson);
            var detailsSvc = new TikTokDetailsSyncService(
                new SimpleHttpClientFactory(new HttpClient(new StaticHandler(RealWireResponseJson))
                {
                    BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
                }),
                Options.Create(new TikTokOptions()),
                db,
                videoSvc,
                NullLogger<TikTokDetailsSyncService>.Instance);

            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP1", limit: 10);

            Assert.Equal(1, processed);

            var metric = await db.ContentMetrics.SingleAsync();
            Assert.Equal(263, metric.Views);
            Assert.Equal(13, metric.Likes);
            Assert.Equal(0, metric.Comments);      // valid zero, not null
            Assert.Equal(0, metric.Shares);
            Assert.Equal(0, metric.NewFollowers);
            Assert.True(metric.Comments.HasValue); // 0 != null in the persisted row
            Assert.Null(metric.Reach);             // unverified fields stay null
            Assert.Null(metric.AverageWatch);
            Assert.Null(metric.FullWatchRate);
            Assert.Null(metric.DemographicsJson);  // viewer_profile [] -> null
        }

        [Fact]
        public async Task RunDetailsSyncAsync_Prioritizes_ViewsOnly_Then_Persists_Enriched_Latest()
        {
            // Proves latest-metric semantics: the views-only snapshot from list sync gets an
            // enriched sibling snapshot, and the NEWEST snapshot (OrderByDescending CapturedAt)
            // carries engagement for the Content Log.
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new KaleContentOps.Models.TikTokShop { ShopCipher = "SHOP2" };
            db.TikTokShops.Add(shop);
            var log = new KaleContentOps.Models.ContentLog
            {
                VideoId = "V-LIST-1",
                TikTokShop = shop
            };
            db.ContentLogs.Add(log);
            await db.SaveChangesAsync();

            // Seed a views-only metric as the list sync would create (Likes etc = null)
            db.ContentMetrics.Add(new KaleContentOps.Models.ContentMetric
            {
                ContentLogId = log.Id,
                Views = 100,
                CapturedAt = DateTime.UtcNow.AddHours(-2)
            });
            await db.SaveChangesAsync();

            var videoSvc = CreateRealVideoService(db, RealWireResponseJson);
            var detailsSvc = new TikTokDetailsSyncService(
                new SimpleHttpClientFactory(new HttpClient(new StaticHandler(RealWireResponseJson))
                {
                    BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
                }),
                Options.Create(new TikTokOptions()),
                db,
                videoSvc,
                NullLogger<TikTokDetailsSyncService>.Instance);

            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP2", limit: 10);
            Assert.Equal(1, processed);

            var metrics = await db.ContentMetrics.Where(m => m.ContentLogId == log.Id).ToListAsync();
            Assert.Equal(2, metrics.Count); // views-only + enriched snapshot

            // Latest metric (what the Content Log query picks) must be the enriched one
            var latest = metrics.OrderByDescending(m => m.CapturedAt).ThenByDescending(m => m.Id).First();
            Assert.Equal(13, latest.Likes);
            Assert.Equal(0, latest.Comments);
            Assert.Equal(263, latest.Views);
        }
    }
}
