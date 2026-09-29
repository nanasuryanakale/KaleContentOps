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

        // REAL response (VideoId 7687996887235890452, captured 2026-09-29) with both viewer profiles:
        // viewer_profile[NEW_FOLLOWER] FIRST, viewer_profile[VIEWERS] SECOND. Sync persistence must
        // select VIEWERS (type field, not array position) and persist its demographics only.
        private const string ViewersWireResponseJson = """
            {
              "code": 0,
              "message": "Success",
              "request_id": "2026092914303716E59FE5AF39751C6DDD",
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "comments": 0,
                        "likes": 15,
                        "new_followers": 1,
                        "shares": 0,
                        "views": 477
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "age_distribution": [
                        { "age": "18-24", "percentage": "0.137" },
                        { "age": "35-44", "percentage": "0.176" }
                      ],
                      "country_distribution": [
                        { "country_code": "", "percentage": "0.002" },
                        { "country_code": "ID", "percentage": "0.990" }
                      ],
                      "gender_distribution": [
                        { "gender": "male", "percentage": "0.543" },
                        { "gender": "female", "percentage": "0.457" }
                      ],
                      "type": "NEW_FOLLOWER"
                    },
                    {
                      "age_distribution": [
                        { "age": "35-44", "percentage": "0.167" },
                        { "age": "25-34", "percentage": "0.500" },
                        { "age": "45-54", "percentage": "0.056" },
                        { "age": "18-24", "percentage": "0.278" }
                      ],
                      "country_distribution": [
                        { "country_code": "ID", "percentage": "1.000" }
                      ],
                      "gender_distribution": [
                        { "gender": "male", "percentage": "0.684" },
                        { "gender": "female", "percentage": "0.316" }
                      ],
                      "type": "VIEWERS"
                    }
                  ]
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

        [Fact]
        public async Task RunDetailsSyncAsync_Persists_Viewers_DemographicsJson()
        {
            // Proves the EXISTING Details Sync flow persists viewer_profile[type=VIEWERS] into
            // ContentMetric.DemographicsJson (real payload shape; NEW_FOLLOWER profile present first
            // and must be excluded by the type selector, not by array position).
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new KaleContentOps.Models.TikTokShop { ShopCipher = "SHOP3" };
            db.TikTokShops.Add(shop);
            db.ContentLogs.Add(new KaleContentOps.Models.ContentLog
            {
                VideoId = "7687996887235890452",
                TikTokShop = shop
            });
            await db.SaveChangesAsync();

            var videoSvc = CreateRealVideoService(db, ViewersWireResponseJson);
            var detailsSvc = new TikTokDetailsSyncService(
                new SimpleHttpClientFactory(new HttpClient(new StaticHandler(ViewersWireResponseJson))
                {
                    BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
                }),
                Options.Create(new TikTokOptions()),
                db,
                videoSvc,
                NullLogger<TikTokDetailsSyncService>.Instance);

            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP3", limit: 10);
            Assert.Equal(1, processed);

            var metric = await db.ContentMetrics.SingleAsync();
            Assert.Equal(477, metric.Views);
            Assert.Equal(15, metric.Likes);
            Assert.NotNull(metric.DemographicsJson);

            // VIEWERS values only (male 0.684 / female 0.316, NOT the NEW_FOLLOWER 0.543/0.457)
            using var demo = JsonDocument.Parse(metric.DemographicsJson!);
            var root = demo.RootElement;
            Assert.Equal(0.684m, root.GetProperty("male").GetDecimal());
            Assert.Equal(0.316m, root.GetProperty("female").GetDecimal());

            var ages = root.GetProperty("ages");
            Assert.Equal(0.278m, ages.GetProperty("18-24").GetDecimal());
            Assert.Equal(0.500m, ages.GetProperty("25-34").GetDecimal());
            Assert.Equal(0.167m, ages.GetProperty("35-44").GetDecimal());
            Assert.Equal(0.056m, ages.GetProperty("45-54").GetDecimal());

            var countries = root.GetProperty("countries");
            Assert.Equal(1.000m, countries.GetProperty("ID").GetDecimal());
        }

        [Fact]
        public async Task RunSingleVideoSyncAsync_Persists_Viewers_Demographics_ForOneVideo()
        {
            // Covers the dev-only single-video mechanism: it must reuse the same pipeline
            // (RunDetailsDiagnosticAsync -> ParseDetailsMetrics -> MapDetailsMetricsToContentMetric
            // -> SaveChangesAsync) and persist VIEWERS demographics for exactly one ContentLog.
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new KaleContentOps.Models.TikTokShop { ShopCipher = "SHOP4" };
            db.TikTokShops.Add(shop);
            var target = new KaleContentOps.Models.ContentLog
            {
                VideoId = "7687996887235890452",
                TikTokShop = shop
            };
            var other = new KaleContentOps.Models.ContentLog
            {
                VideoId = "NOT-TARGETED",
                TikTokShop = shop
            };
            db.ContentLogs.AddRange(target, other);
            await db.SaveChangesAsync();

            var videoSvc = CreateRealVideoService(db, ViewersWireResponseJson);
            var detailsSvc = new TikTokDetailsSyncService(
                new SimpleHttpClientFactory(new HttpClient(new StaticHandler(ViewersWireResponseJson))
                {
                    BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
                }),
                Options.Create(new TikTokOptions()),
                db,
                videoSvc,
                NullLogger<TikTokDetailsSyncService>.Instance);

            var synced = await detailsSvc.RunSingleVideoSyncAsync("7687996887235890452");
            Assert.True(synced);

            // Exactly one metric, on the targeted log only, with VIEWERS demographics.
            var metrics = await db.ContentMetrics.ToListAsync();
            Assert.Single(metrics);
            Assert.Equal(target.Id, metrics[0].ContentLogId);
            Assert.NotNull(metrics[0].DemographicsJson);
            using var demo = JsonDocument.Parse(metrics[0].DemographicsJson!);
            Assert.Equal(0.684m, demo.RootElement.GetProperty("male").GetDecimal());
            Assert.Equal(0.316m, demo.RootElement.GetProperty("female").GetDecimal());
        }
    }
}
