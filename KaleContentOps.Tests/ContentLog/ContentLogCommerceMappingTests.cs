using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using KaleContentOps.Controllers;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using KaleContentOps.Services.TikTok;
using KaleContentOps.ViewModels;
using Xunit;

namespace KaleContentOps.Tests.ContentLogMapping;

// Commerce/attribute metrics verified from App_Data/tiktok-api-sample.json -> data.videos[]:
// gmv{amount,currency}, items_sold, sku_orders, avg_customers, click_through_rate, hash_tags[], products[{id,name}]
public class ContentLogCommerceMappingTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ContentLogListItem> GetFirstItemAsync(AppDbContext db)
    {
        var controller = new ContentLogController(db);
        var result = await controller.Index(null, null);
        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<ContentLogIndexViewModel>(view.Model);
        return Assert.Single(vm.Items);
    }

    // ---------- Controller projection (DB -> ViewModel) ----------

    [Fact]
    public async Task Commerce_Fields_Are_Projected_To_ViewModel()
    {
        var db = CreateDb();
        var log = new ContentLog { VideoId = "V1", Title = "t" };
        db.ContentLogs.Add(log);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 100,
            GmvAmount = 582690.00m,
            GmvCurrency = "IDR",
            ItemsSold = 2,
            SkuOrders = 2,
            AvgCustomers = 1m,
            ClickThroughRate = 0.0533m, // internal 0..1, same convention as FullWatchRate
            HashtagsJson = "[\"kaos\",\"kaospria\"]",
            ProductsJson = "[{\"id\":\"1729430250677569829\",\"name\":\"Kale Miller Knit T-Shirt Cotton Thread Asian Fit\"}]",
            CapturedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(582690.00m, item.GmvAmount);
        Assert.Equal("IDR", item.GmvCurrency);
        Assert.Equal(2, item.ItemsSold);
        Assert.Equal(2, item.SkuOrders);
        Assert.Equal(1m, item.AvgCustomers);
        Assert.Equal(0.0533m, item.ClickThroughRate);

        Assert.NotNull(item.Hashtags);
        Assert.Equal(2, item.Hashtags!.Count);
        Assert.Contains("kaos", item.Hashtags);
        Assert.Contains("kaospria", item.Hashtags);

        Assert.NotNull(item.Products);
        Assert.Single(item.Products!);
        Assert.Equal("1729430250677569829", item.Products[0].Id);
        Assert.Equal("Kale Miller Knit T-Shirt Cotton Thread Asian Fit", item.Products[0].Name);
    }

    [Fact]
    public async Task Latest_Commerce_Snapshot_Wins()
    {
        var db = CreateDb();
        var log = new ContentLog { VideoId = "V1", Title = "t" };
        db.ContentLogs.Add(log);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 10,
                GmvAmount = 100m,
                GmvCurrency = "IDR",
                ItemsSold = 1,
                HashtagsJson = "[\"old\"]",
                CapturedAt = DateTime.UtcNow.AddMinutes(-30) // older
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 20,
                GmvAmount = 582690.00m,
                GmvCurrency = "IDR",
                ItemsSold = 2,
                SkuOrders = 2,
                ClickThroughRate = 0.0533m,
                HashtagsJson = "[\"kaos\"]",
                CapturedAt = DateTime.UtcNow // latest
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(20, item.Views);
        Assert.Equal(582690.00m, item.GmvAmount);
        Assert.Equal(2, item.ItemsSold);
        Assert.Equal(2, item.SkuOrders);
        Assert.Equal(0.0533m, item.ClickThroughRate);
        Assert.Single(item.Hashtags!);
        Assert.Contains("kaos", item.Hashtags!); // "old" belongs to the older snapshot
    }

    [Fact]
    public async Task Zero_Commerce_Values_Are_Preserved()
    {
        var db = CreateDb();
        var log = new ContentLog { VideoId = "V1", Title = "t" };
        db.ContentLogs.Add(log);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 0,
            GmvAmount = 0m,
            GmvCurrency = "IDR",
            ItemsSold = 0,
            SkuOrders = 0,
            AvgCustomers = 0m,
            ClickThroughRate = 0m, // valid zero: renders "0%", not null/"—"
            CapturedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(0m, item.GmvAmount);
        Assert.Equal(0, item.ItemsSold);
        Assert.Equal(0, item.SkuOrders);
        Assert.Equal(0m, item.AvgCustomers);
        Assert.Equal(0m, item.ClickThroughRate);
    }

    [Fact]
    public async Task Missing_Commerce_Fields_Leave_Null()
    {
        var db = CreateDb();
        var log = new ContentLog { VideoId = "V1", Title = "t" };
        db.ContentLogs.Add(log);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 5,
            CapturedAt = DateTime.UtcNow // no commerce data at all
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Null(item.GmvAmount);
        Assert.Null(item.GmvCurrency);
        Assert.Null(item.ItemsSold);
        Assert.Null(item.SkuOrders);
        Assert.Null(item.AvgCustomers);
        Assert.Null(item.ClickThroughRate);
        Assert.Null(item.Hashtags);
        Assert.Null(item.Products);
    }

    [Fact]
    public async Task Empty_Commerce_Arrays_Are_Valid_Empty()
    {
        var db = CreateDb();
        var log = new ContentLog { VideoId = "V1", Title = "t" };
        db.ContentLogs.Add(log);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 5,
            HashtagsJson = "[]",     // empty array is valid, not missing
            ProductsJson = "[]",
            CapturedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.NotNull(item.Hashtags);
        Assert.Empty(item.Hashtags!);
        Assert.NotNull(item.Products);
        Assert.Empty(item.Products!);
    }

    [Fact]
    public async Task Corrupt_Commerce_Json_Leaves_Collections_Null()
    {
        var db = CreateDb();
        var log = new ContentLog { VideoId = "V1", Title = "t" };
        db.ContentLogs.Add(log);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 5,
            HashtagsJson = "not-json{",
            ProductsJson = "{broken",
            CapturedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Null(item.Hashtags);
        Assert.Null(item.Products);
    }

    // ---------- Parser (TikTok response -> DB) ----------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHandler(string json) => _json = json;
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

    private static TikTokVideoService CreateService(AppDbContext db, string responseJson)
    {
        var factory = new SimpleHttpClientFactory(new HttpClient(new StubHandler(responseJson))
        {
            BaseAddress = new Uri("https://open-api.tiktokglobalshop.com")
        });
        var sig = new TikTokSignatureService(Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" }));
        return new TikTokVideoService(factory, Options.Create(new TikTokOptions { AppKey = "K" }), db, new StubAuth(), sig);
    }

    [Fact]
    public async Task Parser_Extracts_Commerce_Fields_With_Invariant_Decimals()
    {
        var db = CreateDb();
        db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
        db.TikTokShops.Add(new TikTokShop { ShopCipher = "SC" });
        await db.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new
            {
                videos = new[]
                {
                    new
                    {
                        video_id = "123",
                        title = "Commerce video",
                        username = "u",
                        video_post_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        duration = 10,
                        // string amounts as sent by the actual API
                        gmv = new { amount = "582690.00", currency = "IDR" },
                        items_sold = 2,
                        sku_orders = "3",          // string-encoded number must parse too
                        avg_customers = "1",
                        click_through_rate = "0.0533",
                        hash_tags = new[] { "kaos", "kaospria" },
                        products = new[]
                        {
                            new { id = "1729430250677569829", name = "Kale Miller Knit T-Shirt Cotton Thread Asian Fit" }
                        }
                    }
                }
            }
        });

        var svc = CreateService(db, json);
        var count = await svc.FetchAndSaveVideoListAsync("SC");
        Assert.Equal(1, count);

        var metric = await db.ContentMetrics.SingleAsync();
        Assert.Equal(582690.00m, metric.GmvAmount);       // string -> decimal, invariant culture
        Assert.Equal("IDR", metric.GmvCurrency);
        Assert.Equal(2, metric.ItemsSold);
        Assert.Equal(3, metric.SkuOrders);
        Assert.Equal(1m, metric.AvgCustomers);
        Assert.Equal(0.0533m, metric.ClickThroughRate);   // stored as 0..1

        Assert.False(string.IsNullOrEmpty(metric.HashtagsJson));
        Assert.Contains("kaos", metric.HashtagsJson);
        Assert.False(string.IsNullOrEmpty(metric.ProductsJson));
        Assert.Contains("Kale Miller Knit T-Shirt", metric.ProductsJson);
    }

    [Fact]
    public async Task Parser_Missing_Commerce_Fields_Leave_Nulls()
    {
        var db = CreateDb();
        db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
        db.TikTokShops.Add(new TikTokShop { ShopCipher = "SC" });
        await db.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new
            {
                videos = new[]
                {
                    new
                    {
                        video_id = "456",
                        title = "No commerce",
                        username = "u",
                        video_post_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        duration = 10,
                        views = 10
                    }
                }
            }
        });

        var svc = CreateService(db, json);
        var count = await svc.FetchAndSaveVideoListAsync("SC");
        Assert.Equal(1, count);

        var metric = await db.ContentMetrics.SingleAsync();
        Assert.Null(metric.GmvAmount);
        Assert.Null(metric.GmvCurrency);
        Assert.Null(metric.ItemsSold);
        Assert.Null(metric.SkuOrders);
        Assert.Null(metric.AvgCustomers);
        Assert.Null(metric.ClickThroughRate);
        Assert.Null(metric.HashtagsJson);
        Assert.Null(metric.ProductsJson);
    }

    [Fact]
    public async Task Parser_Malformed_Commerce_Values_Do_Not_Crash_Sync()
    {
        var db = CreateDb();
        db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
        db.TikTokShops.Add(new TikTokShop { ShopCipher = "SC" });
        await db.SaveChangesAsync();

        var json = @"{
              ""code"": 0,
              ""data"": {
                ""videos"": [
                  {
                    ""video_id"": ""789"",
                    ""title"": ""Malformed"",
                    ""username"": ""u"",
                    ""video_post_time"": 1758500000,
                    ""duration"": 10,
                    ""gmv"": { ""amount"": ""not-a-number"", ""currency"": ""IDR"" },
                    ""click_through_rate"": ""abc"",
                    ""avg_customers"": ""xyz"",
                    ""hash_tags"": [""valid-tag"", """", null],
                    ""products"": [""not-an-object"", { ""id"": ""p1"", ""name"": ""Product One"" }]
                  }
                ]
              }
            }";

        var svc = CreateService(db, json);
        var count = await svc.FetchAndSaveVideoListAsync("SC"); // must not throw
        Assert.Equal(1, count);

        var metric = await db.ContentMetrics.SingleAsync();
        Assert.Null(metric.GmvAmount);          // malformed decimal -> null, sync continues
        Assert.Equal("IDR", metric.GmvCurrency);
        Assert.Null(metric.ClickThroughRate);
        Assert.Null(metric.AvgCustomers);

        Assert.NotNull(metric.HashtagsJson);    // valid entries kept, blanks/null dropped
        Assert.Contains("valid-tag", metric.HashtagsJson);

        Assert.NotNull(metric.ProductsJson);    // non-object entries skipped
        Assert.Contains("Product One", metric.ProductsJson);
    }

    [Fact]
    public async Task Parser_Empty_Commerce_Arrays_Are_Valid_Empty()
    {
        var db = CreateDb();
        db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
        db.TikTokShops.Add(new TikTokShop { ShopCipher = "SC" });
        await db.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new
            {
                videos = new[]
                {
                    new
                    {
                        video_id = "999",
                        title = "Empty arrays",
                        username = "u",
                        video_post_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        duration = 10,
                        hash_tags = Array.Empty<string>(),
                        products = Array.Empty<object>()
                    }
                }
            }
        });

        var svc = CreateService(db, json);
        var count = await svc.FetchAndSaveVideoListAsync("SC");
        Assert.Equal(1, count);

        var metric = await db.ContentMetrics.SingleAsync();
        Assert.Equal("[]", metric.HashtagsJson);   // empty array is valid, not null
        Assert.Equal("[]", metric.ProductsJson);
    }

    [Fact]
    public async Task Parser_Zero_Commerce_Values_Are_Preserved()
    {
        var db = CreateDb();
        db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
        db.TikTokShops.Add(new TikTokShop { ShopCipher = "SC" });
        await db.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new
            {
                videos = new[]
                {
                    new
                    {
                        video_id = "111",
                        title = "Zeros",
                        username = "u",
                        video_post_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        duration = 10,
                        gmv = new { amount = "0.00", currency = "IDR" },
                        items_sold = 0,
                        sku_orders = 0,
                        avg_customers = "0",
                        click_through_rate = "0"
                    }
                }
            }
        });

        var svc = CreateService(db, json);
        var count = await svc.FetchAndSaveVideoListAsync("SC");
        Assert.Equal(1, count);

        var metric = await db.ContentMetrics.SingleAsync();
        Assert.Equal(0.00m, metric.GmvAmount);    // 0 stays 0, not null
        Assert.Equal(0, metric.ItemsSold);
        Assert.Equal(0, metric.SkuOrders);
        Assert.Equal(0m, metric.AvgCustomers);
        Assert.Equal(0m, metric.ClickThroughRate);
    }
}
