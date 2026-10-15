using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using KaleContentOps.Controllers;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.ViewModels;
using Xunit;

namespace KaleContentOps.Tests.ContentLogMapping;

/// <summary>
/// Regression tests for the Content Log field-wise metric resolution fix.
///
/// Root cause covered: ContentMetric has TWO writers with DISJOINT column sets.
///   - list sync (TikTokVideoService list path): Views, commerce (Gmv/ItemsSold/SkuOrders/
///     AvgCustomers/ClickThroughRate/Hashtags/Products), date range - engagement + demographics NULL
///   - details sync (MapDetailsMetricsToContentMetric): engagement + demographics + demographics
///     JSON - commerce NULL, and Views only when the payload reports them ("if (viewsFound)")
/// Reading every scalar from the single newest ContentMetric therefore meant whichever sync ran
/// LAST blanked the fields the other one had already fetched: a list re-sync hid engagement, and a
/// details re-sync hid GMV/ItemsSold/Products (and could hide Views).
///
/// Every scalar is now resolved from the newest snapshot that actually carries a value for it -
/// the same rule the page already applies to demographics - ordered CapturedAt DESC, Id DESC.
///
/// Invariants pinned here:
///   - A views-only newer snapshot never blanks an older fetched engagement value.
///   - A commerce-less newer snapshot (details shape) never blanks older commerce values.
///   - A snapshot without Views never blanks an older observed Views value.
///   - An explicit observed 0 is preserved and never replaced by an older value.
///   - A field no snapshot carries stays NULL - never a fabricated 0.
///   - When the newest snapshot carries a field it still wins (unchanged latest-wins outcome).
///   - Equal CapturedAt breaks deterministically on highest Id.
/// </summary>
public class ContentLogMetricFallbackTests
{
    private const string ProductsJson = "[{\"id\":\"p1\",\"name\":\"Kaos\"}]";
    private const string HashtagsJson = "[\"kaos\"]";

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ContentLog SeedLog(AppDbContext db, string videoId = "V1")
    {
        var log = new ContentLog { VideoId = videoId, Title = "t" };
        db.ContentLogs.Add(log);
        db.SaveChanges();
        return log;
    }

    private static async Task<ContentLogListItem> GetFirstItemAsync(AppDbContext db)
    {
        var controller = new ContentLogController(db);
        var result = await controller.Index(null, null);
        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<ContentLogIndexViewModel>(view.Model);
        return Assert.Single(vm.Items);
    }

    /// <summary>A details-sync shaped snapshot: Views + engagement + demographics, NO commerce.</summary>
    private static ContentMetric EnrichedSnapshot(long logId, DateTime capturedAt, long likes = 13) => new()
    {
        ContentLogId = logId,
        Views = 263,
        Reach = 101,
        Likes = likes,
        Comments = 12,
        Shares = 11,
        NewFollowers = 10,
        AverageWatch = 7.5m,
        FullWatchRate = 0.42m,
        CapturedAt = capturedAt
    };

    /// <summary>A list-sync shaped snapshot: Views + commerce + date range, every engagement column NULL.</summary>
    private static ContentMetric ListSnapshot(long logId, DateTime capturedAt, long views = 485) => new()
    {
        ContentLogId = logId,
        Views = views,
        GmvAmount = 125.50m,
        GmvCurrency = "IDR",
        ItemsSold = 3,
        SkuOrders = 2,
        AvgCustomers = 1.5m,
        ClickThroughRate = 0.0533m,
        HashtagsJson = HashtagsJson,
        ProductsJson = ProductsJson,
        MetricStartDate = new DateTime(2026, 10, 13),
        MetricEndDate = new DateTime(2026, 10, 16),
        CapturedAt = capturedAt
    };

    [Fact]
    public async Task ViewsOnly_Newer_Snapshot_Does_Not_Blank_Fetched_Engagement()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            EnrichedSnapshot(log.Id, new DateTime(2026, 10, 12, 2, 14, 48)),
            ListSnapshot(log.Id, new DateTime(2026, 10, 15, 2, 22, 7)));
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // Engagement was fetched once and must survive the later list sync.
        Assert.Equal(101, item.Reach);
        Assert.Equal(13, item.Likes);
        Assert.Equal(12, item.Comments);
        Assert.Equal(11, item.Shares);
        Assert.Equal(10, item.NewFollowers);
        Assert.Equal(7.5m, item.AverageWatch);
        Assert.Equal(0.42m, item.FullWatchRate);

        // Views come from the newest snapshot that carries them - this one does (485).
        Assert.Equal(485, item.Views);
    }

    [Fact]
    public async Task Engagement_Absent_From_Every_Snapshot_Stays_Null_Never_Zero()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            ListSnapshot(log.Id, new DateTime(2026, 10, 14, 4, 27, 58), views: 11),
            ListSnapshot(log.Id, new DateTime(2026, 10, 15, 2, 22, 7), views: 14));
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // The reported symptom shape: two list snapshots, no details snapshot ever. Nothing is
        // fabricated - every engagement field stays NULL (the view renders a dash, not 0).
        Assert.Null(item.Reach);
        Assert.Null(item.Likes);
        Assert.Null(item.Comments);
        Assert.Null(item.Shares);
        Assert.Null(item.NewFollowers);
        Assert.Null(item.AverageWatch);
        Assert.Null(item.FullWatchRate);

        Assert.Equal(14, item.Views);
        Assert.Equal(125.50m, item.GmvAmount);
    }

    [Fact]
    public async Task Explicit_Zero_Engagement_Observation_Is_Preserved_Over_Older_Value()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            EnrichedSnapshot(log.Id, new DateTime(2026, 10, 12), likes: 99),
            // Newer snapshot observed zero likes: an observation, not an absence.
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 500,
                Likes = 0,
                CapturedAt = new DateTime(2026, 10, 15)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // 0 is kept; the older 99 must not come back. Fields the newer row omits fall back.
        Assert.Equal(0, item.Likes);
        Assert.Equal(101, item.Reach); // only the older snapshot carries Reach
    }

    [Fact]
    public async Task Newest_Snapshot_Carrying_The_Field_Wins_Among_Multiple()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            EnrichedSnapshot(log.Id, new DateTime(2026, 10, 10), likes: 1),
            EnrichedSnapshot(log.Id, new DateTime(2026, 10, 13), likes: 2),
            ListSnapshot(log.Id, new DateTime(2026, 10, 15)));
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // Newest snapshot carrying Likes (the 10-13 enriched one) wins; the views-only newer
        // snapshot carries no Likes so it cannot take part.
        Assert.Equal(2, item.Likes);
    }

    [Fact]
    public async Task Equal_CapturedAt_Tie_Breaks_On_Highest_Id()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        var sameInstant = new DateTime(2026, 10, 14, 8, 0, 0);
        db.ContentMetrics.AddRange(
            EnrichedSnapshot(log.Id, sameInstant, likes: 5),
            EnrichedSnapshot(log.Id, sameInstant, likes: 6),
            ListSnapshot(log.Id, sameInstant));
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // Resolution orders CapturedAt DESC then Id DESC, so every scalar is deterministic even
        // when snapshots share an instant: the last-inserted row (the list snapshot) carries
        // Views, the second-enriched row carries Likes.
        Assert.Equal(485, item.Views);
        Assert.Equal(6, item.Likes);
    }

    [Fact]
    public async Task Commerce_And_Hashtags_Keep_Newest_Snapshot_That_Carries_Them()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        var oldList = ListSnapshot(log.Id, new DateTime(2026, 10, 13), views: 100);
        oldList.ProductsJson = "[{\"id\":\"old\",\"name\":\"Old\"}]";
        oldList.GmvAmount = 999m;

        var newestList = ListSnapshot(log.Id, new DateTime(2026, 10, 16), views: 200);
        newestList.ProductsJson = ProductsJson;

        db.ContentMetrics.AddRange(oldList, newestList);
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // Both snapshots are list-shaped and carry commerce: the newer one still wins.
        Assert.Equal(200, item.Views);
        Assert.Equal(125.50m, item.GmvAmount);
        Assert.Equal(ProductsJson, item.ProductsJson);
        Assert.DoesNotContain("old", item.ProductsJson);
        Assert.Equal(HashtagsJson, item.HashtagsJson);
        Assert.Equal(3, item.ItemsSold);
        Assert.Equal(2, item.SkuOrders);
        Assert.Equal(0.0533m, item.ClickThroughRate);
    }

    [Fact]
    public async Task Details_Sync_Snapshot_Must_Not_Blank_Commerce_From_Older_List_Snapshot()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        // The reverse direction of the reported defect: an older list sync captured commerce...
        db.ContentMetrics.AddRange(
            ListSnapshot(log.Id, new DateTime(2026, 10, 13)),
            // ...then a NEWER details sync wrote engagement but no commerce columns at all.
            EnrichedSnapshot(log.Id, new DateTime(2026, 10, 15)));
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // Commerce survives the newer details snapshot instead of going blank.
        Assert.Equal(125.50m, item.GmvAmount);
        Assert.Equal("IDR", item.GmvCurrency);
        Assert.Equal(3, item.ItemsSold);
        Assert.Equal(2, item.SkuOrders);
        Assert.Equal(1.5m, item.AvgCustomers);
        Assert.Equal(0.0533m, item.ClickThroughRate);
        Assert.Equal(HashtagsJson, item.HashtagsJson);
        Assert.Equal(ProductsJson, item.ProductsJson);

        // ...while engagement comes from that newer details snapshot.
        Assert.Equal(13, item.Likes);
        Assert.Equal(101, item.Reach);
    }

    [Fact]
    public async Task Snapshot_Without_Views_Does_Not_Blank_Older_Observed_Views()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            // Older list snapshot observed the views...
            ListSnapshot(log.Id, new DateTime(2026, 10, 13), views: 485),
            // ...then a newer details snapshot whose payload carried no views block at all
            // (TikTokVideoService only assigns Views when "viewsFound").
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = null,
                Likes = 7,
                CapturedAt = new DateTime(2026, 10, 15)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(485, item.Views); // older observation survives the NULL newest row
        Assert.Equal(7, item.Likes);   // engagement still comes from the newest snapshot
    }
}
