using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using KaleContentOps.Controllers;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using KaleContentOps.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.ContentLogMapping;

/// <summary>
/// Regression tests for the NEW_FOLLOWER demographics projection in Content Log.
///
/// Contract under test (from TikTokVideoService.MapDetailsMetricsToContentMetric):
///   - NEW_FOLLOWER demographics are serialized to ContentMetric.NewFollowerDemographicsJson
///     using the SAME key shape as VIEWERS (male / female / no_gender / ages{18-24..55+} /
///     countries{ISO: share}) but are a strictly separate viewer_profile population.
///   - The projection picks each population's own newest meaningful snapshot
///     (CapturedAt DESC, Id DESC tie-break); a views-only newest metric never hides an
///     older valid snapshot of either population.
///   - Explicit numeric zeros are meaningful data; malformed / "{}" / unsupported shapes
///     are blank, never fabricated.
///   - The two populations can never cross-populate each other's display fields.
/// </summary>
public class ContentLogNewFollowerDisplayTests
{
    private const string ViewersDemo = "{\"male\":0.700,\"female\":0.300,\"ages\":{\"18-24\":0.222,\"25-34\":0.444,\"35-44\":0.333},\"countries\":{\"ID\":1.000}}";
    private const string NewFollowerDemo = "{\"male\":0.100,\"female\":0.900,\"no_gender\":0.0,\"ages\":{\"18-24\":1.000,\"25-34\":0.000},\"countries\":{\"MY\":0.250,\"ID\":0.750}}";
    private const string NewFollowerDemoOlder = "{\"male\":0.400,\"female\":0.600,\"ages\":{\"25-34\":0.500}}";

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
    public async Task NewFollower_JSON_Projects_Into_Its_Own_Fields_Independently()
    {
        // Both populations present on the same metric: each populates its own fields.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 477,
            DemographicsJson = ViewersDemo,
            NewFollowerDemographicsJson = NewFollowerDemo,
            CapturedAt = new DateTime(2026, 10, 1)
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // VIEWERS fields
        Assert.Equal(0.700m, item.Male);
        Assert.Equal(0.300m, item.Female);
        Assert.Null(item.NonGender);
        Assert.Equal(0.222m, item.Age18_24);
        Assert.NotNull(item.Countries);
        Assert.Single(item.Countries!);

        // NEW_FOLLOWER fields
        Assert.Equal(0.100m, item.NfMale);
        Assert.Equal(0.900m, item.NfFemale);
        Assert.Equal(0.0m, item.NfNonGender);
        Assert.Equal(1.000m, item.NfAge18_24);
        Assert.Equal(0.000m, item.NfAge25_34);
        Assert.NotNull(item.NfCountries);
        Assert.Equal(2, item.NfCountries!.Count);
        // Countries sorted by share descending: ID 0.75 before MY 0.25.
        Assert.Equal("ID", item.NfCountries[0].Key);
        Assert.Equal(0.750m, item.NfCountries[0].Value);
        Assert.Equal("MY", item.NfCountries[1].Key);
        Assert.Equal(0.250m, item.NfCountries[1].Value);
    }

    [Fact]
    public async Task ViewsOnly_Newest_Metric_Does_Not_Shadow_Older_NewFollower_Snapshot()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 263,
                NewFollowerDemographicsJson = NewFollowerDemoOlder,
                CapturedAt = new DateTime(2026, 9, 20)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 485, // newer, views-only for BOTH populations
                CapturedAt = new DateTime(2026, 10, 12)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(485, item.Views);              // scalar latest-metric-wins
        Assert.Equal(0.400m, item.NfMale);          // older valid NEW_FOLLOWER survives
        Assert.Equal(0.600m, item.NfFemale);
        Assert.Equal(0.500m, item.NfAge25_34);
        Assert.Null(item.NfCountries);              // no cross-snapshot merging
        Assert.Null(item.Male);                     // VIEWERS fields stay blank (no VIEWERS data)
    }

    [Fact]
    public async Task Newest_Meaningful_NewFollower_Snapshot_Wins()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 1,
                NewFollowerDemographicsJson = NewFollowerDemoOlder,
                CapturedAt = new DateTime(2026, 9, 1)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 2,
                NewFollowerDemographicsJson = NewFollowerDemo,
                CapturedAt = new DateTime(2026, 10, 1)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(0.100m, item.NfMale);     // newest, not older 0.400
        Assert.Equal(0.900m, item.NfFemale);   // newest, not older 0.600
        Assert.Equal(0.000m, item.NfAge25_34); // newest explicit zero, not older 0.500
    }

    [Fact]
    public async Task Equal_CapturedAt_NewFollower_Tie_Breaks_On_Highest_Id()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        var same = new DateTime(2026, 10, 2, 12, 0, 0);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 1,
            NewFollowerDemographicsJson = NewFollowerDemoOlder,
            CapturedAt = same
        });
        await db.SaveChangesAsync();
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 2,
            NewFollowerDemographicsJson = NewFollowerDemo,
            CapturedAt = same
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(0.100m, item.NfMale); // higher-Id (newer inserted) snapshot wins the tie
    }

    [Fact]
    public async Task No_NewFollower_Data_Leaves_Nf_Fields_Blank_And_Viewers_Intact()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 100,
            DemographicsJson = ViewersDemo, // only VIEWERS population present
            CapturedAt = new DateTime(2026, 10, 1)
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Null(item.NewFollowerDemographicsJson);
        Assert.Null(item.NfMale);
        Assert.Null(item.NfFemale);
        Assert.Null(item.NfNonGender);
        Assert.Null(item.NfAge18_24);
        Assert.Null(item.NfAge25_34);
        Assert.Null(item.NfAge35_44);
        Assert.Null(item.NfAge45_54);
        Assert.Null(item.NfAge55Plus);
        Assert.Null(item.NfCountries);

        // VIEWERS fields unaffected.
        Assert.Equal(0.700m, item.Male);
        Assert.Equal(0.300m, item.Female);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"unknown_shape\":1}")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public async Task Unmeaningful_Newest_NewFollower_Json_Falls_Back_Without_Fabrication(string newestJson)
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 10,
                NewFollowerDemographicsJson = NewFollowerDemoOlder,
                CapturedAt = new DateTime(2026, 9, 1)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 20,
                NewFollowerDemographicsJson = newestJson,
                CapturedAt = new DateTime(2026, 10, 1)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(20, item.Views);
        Assert.Equal(0.400m, item.NfMale);   // older valid snapshot preserved
        Assert.Equal(0.600m, item.NfFemale);
    }

    [Fact]
    public async Task Malformed_Only_NewFollower_Json_Leaves_Nf_Fields_Blank()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 7,
            NewFollowerDemographicsJson = "{not-valid-json",
            CapturedAt = new DateTime(2026, 10, 1)
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(7, item.Views);
        Assert.Null(item.NfMale);
        Assert.Null(item.NfFemale);
        Assert.Null(item.NfAge18_24);
        Assert.Null(item.NfCountries);
    }

    [Fact]
    public async Task Explicit_Zeros_In_NewFollower_Snapshot_Are_Meaningful_And_Displayed()
    {
        // Newest NEW_FOLLOWER snapshot with explicit zeros must win (zeros are valid data).
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 10,
                NewFollowerDemographicsJson = NewFollowerDemoOlder,
                CapturedAt = new DateTime(2026, 9, 1)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 0,
                NewFollowerDemographicsJson = "{\"male\":0.0,\"female\":0.0,\"no_gender\":0.0,\"ages\":{\"18-24\":0.0,\"25-34\":0.0,\"35-44\":0.0,\"45-54\":0.0,\"55+\":0.0}}",
                CapturedAt = new DateTime(2026, 10, 2)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(0, item.Views);
        Assert.Equal(0.0m, item.NfMale);
        Assert.Equal(0.0m, item.NfFemale);
        Assert.Equal(0.0m, item.NfNonGender);
        Assert.Equal(0.0m, item.NfAge18_24);
        Assert.Equal(0.0m, item.NfAge25_34);
        Assert.Equal(0.0m, item.NfAge35_44);
        Assert.Equal(0.0m, item.NfAge45_54);
        Assert.Equal(0.0m, item.NfAge55Plus);
    }

    [Fact]
    public async Task Populations_Never_Cross_In_Either_Direction()
    {
        // Newest metric has NEW_FOLLOWER only; older metric has VIEWERS only.
        // Each population must use its own snapshot; neither leaks into the other.
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 100,
                DemographicsJson = ViewersDemo, // male 0.700, age18 0.222
                CapturedAt = new DateTime(2026, 9, 15)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 200,
                NewFollowerDemographicsJson = NewFollowerDemo, // male 0.100, age18 1.000
                CapturedAt = new DateTime(2026, 10, 15)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // VIEWERS fields never show NEW_FOLLOWER values.
        Assert.Equal(0.700m, item.Male);
        Assert.Equal(0.300m, item.Female);
        Assert.Equal(0.222m, item.Age18_24);
        Assert.NotEqual(0.100m, item.Male);
        Assert.NotEqual(1.000m, item.Age18_24);

        // NEW_FOLLOWER fields never show VIEWERS values.
        Assert.Equal(0.100m, item.NfMale);
        Assert.Equal(0.900m, item.NfFemale);
        Assert.Equal(1.000m, item.NfAge18_24);
        Assert.NotEqual(0.700m, item.NfMale);
        Assert.NotEqual(0.222m, item.NfAge18_24);
    }

    [Fact]
    public async Task Ordinary_Scalars_Keep_Latest_Metric_Wins_With_NewFollower_Fallback()
    {
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
                NewFollowerDemographicsJson = NewFollowerDemoOlder,
                CapturedAt = new DateTime(2026, 9, 1)
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 999,
                Likes = 21,
                Comments = 22,
                Shares = 23,
                NewFollowers = 24,
                CapturedAt = new DateTime(2026, 10, 3)
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(999, item.Views);
        Assert.Equal(21, item.Likes);
        Assert.Equal(22, item.Comments);
        Assert.Equal(23, item.Shares);
        Assert.Equal(24, item.NewFollowers);
        // ...while NEW_FOLLOWER demographics still fall back to the older valid snapshot.
        Assert.Equal(0.400m, item.NfMale);
        Assert.Equal(0.600m, item.NfFemale);
    }
}

/// <summary>
/// Renders the real /ContentLog page through the MVC pipeline (EF InMemory, seeded fixtures)
/// and asserts the NEW_FOLLOWER columns are present, clearly labeled as the new-follower
/// population, strictly separated from the VIEWERS columns, and wired into the existing
/// column-visibility control (same localStorage key, default-off for the new columns).
/// No TikTok API, no sync, no real database: fixtures only.
/// </summary>
public class ContentLogNewFollowerRenderTests
{
    private const string RowMarker = "nf-render-row";
    private const string ViewersDemo = "{\"male\":0.700,\"female\":0.300,\"ages\":{\"18-24\":0.222,\"25-34\":0.444,\"35-44\":0.333},\"countries\":{\"ID\":1.000}}";
    private const string NewFollowerDemo = "{\"male\":0.100,\"female\":0.900,\"ages\":{\"18-24\":1.000},\"countries\":{\"MY\":0.250,\"ID\":0.750}}";
    private const string Dash = "&#x2014;";

    private static async Task<string> RenderAsync(Action<AppDbContext> seed)
    {
        var factory = new AuthTestFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            seed(db);
            db.SaveChanges();
        }

        var username = ("nf.render." + Guid.NewGuid().ToString("N")).Substring(0, 24);
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);
        return await client.GetStringAsync("/ContentLog");
    }

    private static void SeedBothPopulationsRow(AppDbContext db)
    {
        var log = new ContentLog
        {
            VideoId = "v-nf-render",
            Title = RowMarker,
            Username = "kaleofficial",
            VideoPostTime = new DateTime(2026, 10, 1, 12, 0, 0)
        };
        db.ContentLogs.Add(log);
        db.SaveChanges();
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 477,
            DemographicsJson = ViewersDemo,
            NewFollowerDemographicsJson = NewFollowerDemo,
            CapturedAt = new DateTime(2026, 10, 2, 12, 0, 0)
        });
        db.SaveChanges();
    }

    /// <summary>Returns the rendered &lt;tr&gt; containing the given cell text.</summary>
    private static string Row(string html, string marker)
    {
        var m = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(m > 0, $"row marker not found: {marker}");

        var rowStart = html.LastIndexOf("<tr data-content-id", m, StringComparison.Ordinal);
        Assert.True(rowStart >= 0, "row start not found");

        var rowEnd = html.IndexOf("</tr>", m, StringComparison.Ordinal);
        Assert.True(rowEnd > rowStart, "row end not found");

        return html.Substring(rowStart, rowEnd - rowStart);
    }

    /// <summary>Returns the trimmed text of the first &lt;td data-col="col"&gt; inside a row.</summary>
    private static string Cell(string row, string col)
    {
        var marker = $"data-col=\"{col}\"";
        var i = row.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i > 0, $"column not found in row: {col}");

        var open = row.IndexOf('>', i);
        var close = row.IndexOf("</td>", open, StringComparison.Ordinal);
        Assert.True(close > open, "cell end not found");

        return row.Substring(open + 1, close - open - 1).Trim();
    }

    [Fact]
    public async Task Render_Shows_Both_Populations_In_Separate_Labeled_Columns()
    {
        var html = await RenderAsync(SeedBothPopulationsRow);

        // Distinct group headers make the population explicit.
        Assert.Contains("DATA PENONTON (%)", html);
        Assert.Contains("PENGIKUT BARU (%)", html);

        // All supported NEW_FOLLOWER columns exist (gender, age groups, country).
        foreach (var col in new[] { "nfmale", "nffemale", "nfnongender", "nfage18", "nfage25", "nfage35", "nfage45", "nfage55", "nfcountry" })
        {
            Assert.Contains($"data-col=\"{col}\"", html);
        }

        // Cell values render per population, strictly separated.
        var row = Row(html, RowMarker);
        Assert.Equal("477", Cell(row, "views"));
        Assert.Equal("70.0%", Cell(row, "male"));
        Assert.Equal("30.0%", Cell(row, "female"));
        Assert.Equal("22.2%", Cell(row, "age18"));
        Assert.Equal("ID 100.0%", Cell(row, "country"));
        Assert.Equal("10.0%", Cell(row, "nfmale"));
        Assert.Equal("90.0%", Cell(row, "nffemale"));
        Assert.Equal("100.0%", Cell(row, "nfage18"));
        Assert.Equal("ID 75.0%, MY 25.0%", Cell(row, "nfcountry"));
        // Non-gender was not present in either payload -> dash, never fabricated 0%.
        Assert.Equal(Dash, Cell(row, "nfnongender"));
    }

    [Fact]
    public async Task Render_NewFollower_Absent_Shows_Dash_And_Viewers_Unaffected()
    {
        var html = await RenderAsync(db =>
        {
            var log = new ContentLog
            {
                VideoId = "v-viewers-only",
                Title = "viewers-only-row",
                Username = "kaleofficial",
                VideoPostTime = new DateTime(2026, 10, 1, 12, 0, 0)
            };
            db.ContentLogs.Add(log);
            db.SaveChanges();
            db.ContentMetrics.Add(new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 100,
                DemographicsJson = ViewersDemo,
                CapturedAt = new DateTime(2026, 10, 2, 12, 0, 0)
            });
        });

        var row = Row(html, "viewers-only-row");
        Assert.Equal("70.0%", Cell(row, "male"));
        Assert.Equal(Dash, Cell(row, "nfmale"));
        Assert.Equal(Dash, Cell(row, "nffemale"));
        Assert.Equal(Dash, Cell(row, "nfage18"));
        Assert.Equal(Dash, Cell(row, "nfcountry"));
    }

    [Fact]
    public async Task Render_NewFollower_Columns_Are_In_Column_Picker_Default_Off()
    {
        var html = await RenderAsync(db => { });

        // Every NEW_FOLLOWER column is discoverable in the column picker with a label that
        // names the population, and defaults to OFF so existing users' layouts are preserved.
        foreach (var (key, label) in new[]
        {
            ("nfmale", "Male (Pengikut Baru)"),
            ("nffemale", "Female (Pengikut Baru)"),
            ("nfnongender", "Non-Gender (Pengikut Baru)"),
            ("nfage18", "18-24 (Pengikut Baru)"),
            ("nfage25", "25-34 (Pengikut Baru)"),
            ("nfage35", "35-44 (Pengikut Baru)"),
            ("nfage45", "45-54 (Pengikut Baru)"),
            // Note: Razor's default HTML encoder renders '+' as &#x2B; (same as the
            // existing VIEWERS "55+" picker entry, which is asserted the same way).
            ("nfage55", "55&#x2B; (Pengikut Baru)"),
            ("nfcountry", "Country (Pengikut Baru)")
        })
        {
            var marker = $"data-col-key=\"{key}\"";
            var i = html.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(i > 0, $"picker toggle missing for {key}");

            // The <label> slice must contain the population name and NO checked attribute
            // (default-off; existing localStorage entries gain the new key via defaults).
            var labelStart = html.LastIndexOf("<label", i, StringComparison.Ordinal);
            var labelEnd = html.IndexOf("</label>", i, StringComparison.Ordinal);
            Assert.True(labelStart >= 0 && labelEnd > labelStart, $"label slice missing for {key}");
            var slice = html.Substring(labelStart, labelEnd - labelStart);
            Assert.Contains("Pengikut Baru", slice);
            Assert.DoesNotContain("checked", slice);

            Assert.Contains(label, slice);
        }

        // Column-visibility mechanism unchanged: same localStorage key and toggle wiring,
        // so existing users keep every previous preference.
        Assert.Contains("contentlog_columns_visibility_v2", html);
        Assert.Contains("class=\"col-toggle\"", html);

        // VIEWERS picker defaults unchanged (male/female stay default-on).
        var maleIdx = html.IndexOf("data-col-key=\"male\"", StringComparison.Ordinal);
        Assert.True(maleIdx > 0);
        var maleStart = html.LastIndexOf("<label", maleIdx, StringComparison.Ordinal);
        var maleEnd = html.IndexOf("</label>", maleIdx, StringComparison.Ordinal);
        Assert.Contains("checked", html.Substring(maleStart, maleEnd - maleStart));
    }
}
