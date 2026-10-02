using System;
using System.Globalization;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.ContentLogDisplay;

/// <summary>
/// Renders the real /ContentLog page through the MVC pipeline (EF InMemory, seeded fixtures)
/// and asserts how stored ContentMetric values reach the browser:
///   - populated value  -> rendered number / percentage
///   - explicit zero    -> rendered "0" / "0.0%"
///   - NULL             -> rendered em dash (never 0)
///   - DemographicsJson -> parsed percentages, raw JSON never emitted
/// No TikTok API, no sync: fixtures only.
/// </summary>
public class ContentLogEnrichedDisplayTests
{
    private const string EnrichedTitle = "Baris enriched (explicit zero)";
    private const string NullTitle = "Baris NULL state";
    // The Razor view emits the em dash through an @(...) expression, so it arrives HTML-encoded.
    private const string Dash = "&#x2014;";

    private static string ExpectedPercent(decimal share) =>
        (share * 100).ToString("0.0", CultureInfo.CurrentCulture) + "%";

    private static async Task<string> RenderAsync(Action<AppDbContext> seed)
    {
        var factory = new AuthTestFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            seed(db);
            db.SaveChanges();
        }

        var username = ("cl.display." + Guid.NewGuid().ToString("N")).Substring(0, 24);
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);
        return await client.GetStringAsync("/ContentLog");
    }

    private static void SeedBothRows(AppDbContext db)
    {
        var enriched = new ContentLog
        {
            VideoId = "v-enriched",
            Title = EnrichedTitle,
            Username = "kaleofficial",
            VideoPostTime = new DateTime(2026, 4, 22, 18, 29, 30)
        };
        var nullState = new ContentLog
        {
            VideoId = "v-null",
            Title = NullTitle,
            Username = "kaleofficial",
            VideoPostTime = new DateTime(2026, 4, 23, 9, 0, 0)
        };
        db.ContentLogs.AddRange(enriched, nullState);
        db.SaveChanges();

        // Older snapshot that must lose against the newer enriched one.
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = enriched.Id,
            Views = 111,
            Reach = 111,
            Likes = 111,
            CapturedAt = new DateTime(2026, 9, 30, 10, 0, 0)
        });
        // Latest snapshot: populated reach/comments/demographics plus explicit zeros.
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = enriched.Id,
            Views = 477,
            Reach = 263,
            Likes = 0,
            Comments = 13,
            Shares = 0,
            NewFollowers = 0,
            DemographicsJson = "{\"male\":0.700,\"female\":0.300,\"ages\":{\"18-24\":0.222,\"25-34\":0.444,\"35-44\":0.333},\"countries\":{\"ID\":1.000}}",
            CapturedAt = new DateTime(2026, 10, 1, 2, 40, 37)
        });
        // NULL-state row: only Views stored, every other metric and demographics are NULL.
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = nullState.Id,
            Views = 477,
            CapturedAt = new DateTime(2026, 10, 1, 3, 0, 0)
        });
        db.SaveChanges();
    }

    /// <summary>Finds the rendered &lt;tr&gt; that contains the given cell text.</summary>
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
    public async Task Populated_Metric_Fields_Are_Rendered_With_Values()
    {
        var html = await RenderAsync(SeedBothRows);
        var row = Row(html, EnrichedTitle);

        Assert.Equal("477", Cell(row, "views"));
        Assert.Equal("263", Cell(row, "reach"));
        Assert.Equal("13", Cell(row, "comments"));
    }

    [Fact]
    public async Task Explicit_Zero_Renders_As_Zero_And_Null_Renders_As_Dash()
    {
        var html = await RenderAsync(SeedBothRows);

        var zeroRow = Row(html, EnrichedTitle);
        Assert.Equal("0", Cell(zeroRow, "likes"));
        Assert.Equal("0", Cell(zeroRow, "share"));
        Assert.Equal("0", Cell(zeroRow, "follower"));

        var nullRow = Row(html, NullTitle);
        Assert.Equal(Dash, Cell(nullRow, "reach"));
        Assert.Equal(Dash, Cell(nullRow, "likes"));
        Assert.Equal(Dash, Cell(nullRow, "comments"));
        Assert.Equal(Dash, Cell(nullRow, "share"));
        Assert.Equal(Dash, Cell(nullRow, "follower"));
        Assert.Equal(Dash + "s", Cell(nullRow, "avgwatch")); // view renders "—s" for a null average watch
        Assert.Equal(Dash, Cell(nullRow, "fullwatch"));
        Assert.Equal(Dash, Cell(nullRow, "male"));
        Assert.Equal(Dash, Cell(nullRow, "country"));
        Assert.Equal("477", Cell(nullRow, "views"));
    }

    [Fact]
    public async Task Demographics_Are_Rendered_As_Percentages_Never_Raw_Json()
    {
        var html = await RenderAsync(SeedBothRows);
        var row = Row(html, EnrichedTitle);

        Assert.Equal(ExpectedPercent(0.700m), Cell(row, "male"));
        Assert.Equal(ExpectedPercent(0.300m), Cell(row, "female"));
        Assert.Equal(ExpectedPercent(0.444m), Cell(row, "age25"));
        Assert.Equal("ID " + ExpectedPercent(1.000m), Cell(row, "country"));

        // Raw JSON must never reach the browser.
        Assert.DoesNotContain("\"male\":0.700", html);
        Assert.DoesNotContain("\"countries\":", html);
    }

    [Fact]
    public async Task Latest_ContentMetric_Is_The_One_Displayed()
    {
        var html = await RenderAsync(SeedBothRows);
        var row = Row(html, EnrichedTitle);

        // Older snapshot (Views 111 / Reach 111 / Likes 111) must not win.
        Assert.Equal("477", Cell(row, "views"));
        Assert.Equal("263", Cell(row, "reach"));
        Assert.Equal("0", Cell(row, "likes"));
    }
}
