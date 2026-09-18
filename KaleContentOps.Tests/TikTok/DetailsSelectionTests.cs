using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;

namespace KaleContentOps.TikTok.Tests
{
    public class DetailsSelectionTests
    {
        class FakeHttpFactory : System.Net.Http.IHttpClientFactory
        {
            public System.Net.Http.HttpClient CreateClient(string name) => new System.Net.Http.HttpClient();
        }

        class FakeVideoService : ITikTokVideoService
        {
            public List<string> CalledVideoIds { get; } = new List<string>();

            public Task<int> FetchAndSaveVideoListAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default) => Task.FromResult(0);
            public Task<DryRunReport> DryRunVideoClassificationAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default) => Task.FromResult(new DryRunReport());
            public Task<(int? StatusCode, string Endpoint, System.Text.Json.JsonElement? Root, System.Text.Json.JsonElement? Matched)> RunPerformanceDiagnosticAsync(string videoId, string shopCipher, CancellationToken cancellationToken = default) => Task.FromResult<(int?, string, System.Text.Json.JsonElement?, System.Text.Json.JsonElement?)>((null, string.Empty, null, null));

            public Task<(int? StatusCode, string Endpoint, System.Text.Json.JsonElement? Root, System.Text.Json.JsonElement? Data)> RunDetailsDiagnosticAsync(string videoId, string shopCipher, CancellationToken cancellationToken = default)
            {
                CalledVideoIds.Add(videoId);
                // Return 200 but no Data to allow RunDetailsSyncAsync to continue without throwing
                return Task.FromResult<(int?, string, System.Text.Json.JsonElement?, System.Text.Json.JsonElement?)>((200, string.Empty, null, null));
            }
        }

        [Fact]
        public async Task Selection_Order_Should_Be_NoMetric_Then_ViewsOnly_Then_Enriched()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var shop = new TikTokShop { ShopCipher = "SHOP1" };

            using (var db = new AppDbContext(options))
            {
                db.TikTokShops.Add(shop);
                db.SaveChanges();

                // ContentLog A: no metric
                var a = new ContentLog { VideoId = "A", TikTokShop = shop };
                // ContentLog B: views-only
                var b = new ContentLog { VideoId = "B", TikTokShop = shop };
                // ContentLog C: enriched
                var c = new ContentLog { VideoId = "C", TikTokShop = shop };

                db.ContentLogs.AddRange(a, b, c);
                db.SaveChanges();

                // Add views-only metric for B
                var mb = new ContentMetric { ContentLogId = b.Id, Views = 15, CapturedAt = DateTime.UtcNow.AddMinutes(-5) };
                // Add enriched metric for C
                var mc = new ContentMetric { ContentLogId = c.Id, Views = 15, Likes = 0, Comments = 0, Shares = 0, NewFollowers = 0, DemographicsJson = "{}", CapturedAt = DateTime.UtcNow.AddMinutes(-10) };
                db.ContentMetrics.AddRange(mb, mc);
                db.SaveChanges();
            }

            var fakeVideoSvc = new FakeVideoService();
            var detailsSvc = new TikTokDetailsSyncService(new FakeHttpFactory(), Options.Create(new TikTokOptions()), new AppDbContext(options), fakeVideoSvc, NullLogger<TikTokDetailsSyncService>.Instance);

            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP1", limit: 10);

            // The fake video service recorded the order of attempted detail calls
            // First should be A (no metric), then B (views-only), then C (enriched)
            Assert.True(fakeVideoSvc.CalledVideoIds.Count >= 3, "Expected at least 3 video detail attempts");
            var firstThree = fakeVideoSvc.CalledVideoIds.Take(3).ToArray();
            Assert.Equal("A", firstThree[0]);
            Assert.Equal("B", firstThree[1]);
            Assert.Equal("C", firstThree[2]);
        }

        [Fact]
        public async Task Selection_Order_Within_Priority_Should_Prefer_Newer_VideoPostTime()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var shop = new TikTokShop { ShopCipher = "SHOP2" };

            using (var db = new AppDbContext(options))
            {
                db.TikTokShops.Add(shop);
                db.SaveChanges();

                // Create three ContentLogs with no metrics but different VideoPostTime
                var older = new ContentLog { VideoId = "V1", TikTokShop = shop, VideoPostTime = DateTime.UtcNow.AddDays(-10) };
                var middle = new ContentLog { VideoId = "V2", TikTokShop = shop, VideoPostTime = DateTime.UtcNow.AddDays(-5) };
                var newest = new ContentLog { VideoId = "V3", TikTokShop = shop, VideoPostTime = DateTime.UtcNow.AddDays(-1) };

                db.ContentLogs.AddRange(older, middle, newest);
                db.SaveChanges();
            }

            var fakeVideoSvc = new FakeVideoService();
            var detailsSvc = new TikTokDetailsSyncService(new FakeHttpFactory(), Options.Create(new TikTokOptions()), new AppDbContext(options), fakeVideoSvc, NullLogger<TikTokDetailsSyncService>.Instance);

            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP2", limit: 10);

            Assert.True(fakeVideoSvc.CalledVideoIds.Count >= 3, "Expected at least 3 video detail attempts");
            var firstThree = fakeVideoSvc.CalledVideoIds.Take(3).ToArray();

            // Expect newest first
            Assert.Equal("V3", firstThree[0]);
            Assert.Equal("V2", firstThree[1]);
            Assert.Equal("V1", firstThree[2]);
        }
    }
}
