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
/// Regression tests for the Content Log demographic projection fix: VIEWERS demographics
/// are resolved from the most recent VALID, meaningful demographic snapshot per ContentLog
/// instead of blindly from the latest ContentMetric.
///
/// Root cause covered: a newer views-only metric (DemographicsJson NULL - the ingestion
/// representation of "no demographic observation", e.g. viewer_profile=[]) used to shadow
/// an older enriched snapshot and render dashes, even though valid demographic data existed.
///
/// Invariants pinned here:
///   - Ordinary metrics (Views, Likes, Comments, Shares, NewFollowers) keep
///     latest-metric-wins semantics, unaffected by the demographics fallback.
///   - The newest meaningful demographic snapshot wins (including explicit zeros).
///   - Malformed / "{}" / unrecognized newest JSON never fabricates values and never
///     shadows an older valid snapshot.
///   - NEW_FOLLOWER demographics (NewFollowerDemographicsJson) are a separate population
///     and are never projected into the VIEWERS fields.
/// </summary>
public class ContentLogDemographicsFallbackTests
{
    private const string ValidDemoA = "{\"male\":0.700,\"female\":0.300,\"ages\":{\"18-24\":0.222,\"25-34\":0.444,\"35-44\":0.333},\"countries\":{\"ID\":1.000}}";
    private const string ValidDemoB = "{\"male\":0.400,\"female\":0.600,\"ages\":{\"25-34\":0.250,\"55+\":0.100}}";

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

    [Fact]
    public async Task ViewsOnly_Newest_Metric_Does_Not_Shadow_Older_Valid_Demographics()
    {
        // Mirrors the production defect (ContentLog 16025): older enriched snapshot,
        // newer views-only snapshot. Views must come from the newest metric; demographics
        // from the older valid snapshot.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 263,
                Likes = 13,
                Comments = 0,
                Shares = 0,
                NewFollowers = 0,
                DemographicsJson = ValidDemoA,
                CapturedAt = new DateTime(2026, 9, 29, 9, 23, 47)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 485, // newer, views-only, DemographicsJson NULL
                CapturedAt = new DateTime(2026, 10, 12, 2, 14, 48)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // Ordinary metrics: latest-metric-wins preserved.
        Assert.Equal(485, item.Views);
        Assert.Null(item.Likes); // newest metric's NULL, NOT the older 13

        // Demographics: older valid snapshot survives the views-only newest metric.
        Assert.NotNull(item.DemographicsJson);
        Assert.Equal(0.700m, item.Male);
        Assert.Equal(0.300m, item.Female);
        Assert.Equal(0.222m, item.Age18_24);
        Assert.Equal(0.444m, item.Age25_34);
        Assert.Equal(0.333m, item.Age35_44);
        Assert.NotNull(item.Countries);
        Assert.Single(item.Countries!);
        Assert.Equal("ID", item.Countries![0].Key);
    }

    [Fact]
    public async Task Newest_Meaningful_Demographic_Snapshot_Wins_Among_Multiple()
    {
        // Three metrics: oldest valid (A), middle valid (B), newest views-only.
        // Demographics must come from B (the newest meaningful), never A; Views from newest.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 100,
                DemographicsJson = ValidDemoA,
                CapturedAt = new DateTime(2026, 9, 1, 0, 0, 0)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 200,
                DemographicsJson = ValidDemoB,
                CapturedAt = new DateTime(2026, 9, 15, 0, 0, 0)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 300,
                CapturedAt = new DateTime(2026, 10, 1, 0, 0, 0)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(300, item.Views);
        Assert.Equal(0.400m, item.Male);      // from B, not A (0.700)
        Assert.Equal(0.600m, item.Female);    // from B, not A (0.300)
        Assert.Equal(0.250m, item.Age25_34);  // from B
        Assert.Equal(0.100m, item.Age55Plus); // from B
        Assert.Null(item.Age18_24);           // only in A; A must NOT be merged in
        Assert.Null(item.Countries);          // only in A; no cross-snapshot merging
    }

    [Fact]
    public async Task Newest_Metric_With_Valid_Demographics_Is_Displayed()
    {
        // The newest metric itself carries valid demographics: it must win.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 100,
                DemographicsJson = ValidDemoA,
                CapturedAt = new DateTime(2026, 9, 1, 0, 0, 0)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 500,
                DemographicsJson = ValidDemoB,
                CapturedAt = new DateTime(2026, 10, 5, 0, 0, 0)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(500, item.Views);
        Assert.Equal(0.400m, item.Male);
        Assert.Equal(0.600m, item.Female);
        Assert.Equal(0.250m, item.Age25_34);
        Assert.Equal(0.100m, item.Age55Plus);
        Assert.Null(item.Age18_24);   // B has no 18-24; must not fall back into A
        Assert.Null(item.Countries);  // B has no countries; must not fall back into A
    }

    [Fact]
    public async Task Equal_CapturedAt_Tie_Breaks_On_Highest_Id()
    {
        // Canonical ordering: CapturedAt DESC, then Id DESC (same tie-break used elsewhere
        // in the project's metric lookups).
        var db = CreateDb();
        var log = SeedLog(db);
        var same = new DateTime(2026, 10, 2, 12, 0, 0);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 1,
            DemographicsJson = ValidDemoA,
            CapturedAt = same
        });
        await db.SaveChangesAsync();
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 2,
            DemographicsJson = ValidDemoB,
            CapturedAt = same
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(0.400m, item.Male); // higher-Id snapshot (B) wins the tie
    }

    [Fact]
    public async Task No_Valid_Demographic_Snapshot_Leaves_Fields_Blank()
    {
        // Never any demographic data: fields stay null (UI convention renders em dash).
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric { ContentLogId = log.Id, Views = 5, CapturedAt = new DateTime(2026, 9, 1) },
            new ContentMetric { ContentLogId = log.Id, Views = 9, CapturedAt = new DateTime(2026, 10, 1) });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(9, item.Views);
        Assert.Null(item.DemographicsJson);
        Assert.Null(item.Male);
        Assert.Null(item.Female);
        Assert.Null(item.NonGender);
        Assert.Null(item.Age18_24);
        Assert.Null(item.Age25_34);
        Assert.Null(item.Age35_44);
        Assert.Null(item.Age45_54);
        Assert.Null(item.Age55Plus);
        Assert.Null(item.Countries);
    }

    [Theory]
    [InlineData("{}")]                                  // empty-but-valid JSON: unpopulated
    [InlineData("{\"unknown_shape\":1}")]               // no recognized demographic keys
    [InlineData("not json at all")]                     // malformed
    [InlineData("[1,2,3]")]                             // wrong root kind
    public async Task Unmeaningful_Newest_Demographics_Never_Shadow_Or_Fabricate(string newestJson)
    {
        // A newest demographic payload that is malformed or semantically unpopulated must
        // not crash, must not fabricate values, and must not erase the older valid snapshot.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 263,
                DemographicsJson = ValidDemoA,
                CapturedAt = new DateTime(2026, 9, 29, 0, 0, 0)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 485,
                DemographicsJson = newestJson,
                CapturedAt = new DateTime(2026, 10, 12, 0, 0, 0)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(485, item.Views);
        Assert.Equal(0.700m, item.Male);   // older valid snapshot preserved
        Assert.Equal(0.300m, item.Female);
    }

    [Fact]
    public async Task Malformed_Only_Newest_Demographics_Leave_Fields_Blank()
    {
        // Malformed payload with no older valid snapshot: blank fields, no crash,
        // no fabricated zeros, scalars unaffected.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 7,
            DemographicsJson = "{not-valid-json",
            CapturedAt = new DateTime(2026, 10, 1)
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(7, item.Views);
        Assert.Null(item.Male);
        Assert.Null(item.Female);
        Assert.Null(item.Age18_24);
        Assert.Null(item.Countries);
    }

    [Fact]
    public async Task Explicit_Zero_Demographics_Are_Meaningful_And_Displayed()
    {
        // Explicit zeros are valid source data (existing contract: Zero_Is_Valid_...):
        // an all-zero newest snapshot must win and render zeros, not fall back.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 100,
                DemographicsJson = ValidDemoA,
                CapturedAt = new DateTime(2026, 9, 1, 0, 0, 0)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 0,
                Likes = 0,
                DemographicsJson = "{\"male\":0.0,\"female\":0.0,\"no_gender\":0.0,\"ages\":{\"18-24\":0.0,\"25-34\":0.0,\"35-44\":0.0,\"45-54\":0.0,\"55+\":0.0}}",
                CapturedAt = new DateTime(2026, 10, 2, 0, 0, 0)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(0, item.Views);
        Assert.Equal(0, item.Likes);
        Assert.Equal(0.0m, item.Male);
        Assert.Equal(0.0m, item.Female);
        Assert.Equal(0.0m, item.NonGender);
        Assert.Equal(0.0m, item.Age18_24);
        Assert.Equal(0.0m, item.Age25_34);
        Assert.Equal(0.0m, item.Age35_44);
        Assert.Equal(0.0m, item.Age45_54);
        Assert.Equal(0.0m, item.Age55Plus);
    }

    [Fact]
    public async Task Ordinary_Metrics_Keep_Latest_Metric_Wins_Regardless_Of_Demographics()
    {
        // Views/Likes/Comments/Shares/NewFollowers always come from the newest metric,
        // even while demographics fall back to the older snapshot.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 100,
                Likes = 11,
                Comments = 12,
                Shares = 13,
                NewFollowers = 14,
                DemographicsJson = ValidDemoA,
                CapturedAt = new DateTime(2026, 9, 1, 0, 0, 0)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 999,
                Likes = 21,
                Comments = 22,
                Shares = 23,
                NewFollowers = 24,
                CapturedAt = new DateTime(2026, 10, 3, 0, 0, 0)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(999, item.Views);
        Assert.Equal(21, item.Likes);
        Assert.Equal(22, item.Comments);
        Assert.Equal(23, item.Shares);
        Assert.Equal(24, item.NewFollowers);
        // ...while VIEWERS demographics still come from the older valid snapshot.
        Assert.Equal(0.700m, item.Male);
        Assert.Equal(0.300m, item.Female);
    }

    [Fact]
    public async Task NewFollower_Demographics_Never_Populate_Viewers_Fields()
    {
        // NEW_FOLLOWER JSON is a separate population: it must never be projected into the
        // VIEWERS gender/age/country fields, and its absence must not block VIEWERS fallback.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 263,
                DemographicsJson = ValidDemoA,
                CapturedAt = new DateTime(2026, 9, 20, 0, 0, 0)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 485,
                NewFollowerDemographicsJson = "{\"male\":0.100,\"female\":0.900,\"ages\":{\"18-24\":1.000}}",
                CapturedAt = new DateTime(2026, 10, 12, 0, 0, 0)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(485, item.Views);
        // VIEWERS fields from the VIEWERS snapshot only.
        Assert.Equal(0.700m, item.Male);
        Assert.Equal(0.300m, item.Female);
        Assert.Equal(0.222m, item.Age18_24);
        // Never the NEW_FOLLOWER values (0.100 / 0.900 / 1.000).
        Assert.NotEqual(0.100m, item.Male);
        Assert.NotEqual(0.900m, item.Female);
        Assert.NotEqual(1.000m, item.Age18_24);
    }
}
