using System;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.ContentLogDisplay;

/// <summary>
/// Renders the real /ContentLog page through the MVC pipeline and asserts the UI cleanup:
///   - no Date Range filter UI (mockup only has the search bar + existing filters)
///   - no KOMERSIL &amp; ATRIBUT group headers, cells or column-visibility toggles
///   - search bar, remaining column groups and localStorage visibility key stay intact
/// Presentation-only removal: DB, EF models, DTOs and controller projections unchanged.
/// No TikTok API, no sync: fixtures only.
/// </summary>
public class ContentLogUiCleanupTests
{
    private const string Dash = "&#x2014;";

    private static async Task<string> RenderPageAsync(Action<AppDbContext>? seed = null)
    {
        var factory = new AuthTestFactory();

        if (seed != null)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                seed(db);
                db.SaveChanges();
            }
        }

        var username = ("cl.ui." + Guid.NewGuid().ToString("N")).Substring(0, 24);
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);
        return await client.GetStringAsync("/ContentLog");
    }

    [Fact]
    public async Task DateRange_Filter_Ui_Is_Gone_But_Search_Bar_Remains()
    {
        var html = await RenderPageAsync();

        // No date range controls, labels or inputs anywhere on the page.
        Assert.DoesNotContain("date-filter", html);
        Assert.DoesNotContain("name=\"dateFrom\"", html);
        Assert.DoesNotContain("name=\"dateTo\"", html);
        Assert.DoesNotContain("type=\"date\"", html);

        // The search bar itself must remain, still posting to the server.
        Assert.Contains("name=\"search\"", html);
        Assert.Contains("class=\"search-box\"", html);
        Assert.Contains("method=\"get\"", html);
    }

    [Fact]
    public async Task Komersil_Atribut_Group_Is_Gone_From_Headers_Cells_And_Toggles()
    {
        var html = await RenderPageAsync();

        // Group header removed.
        Assert.DoesNotContain("KOMERSIL", html);
        Assert.DoesNotContain("commerce-head", html);

        // Column-visibility toggles for the commercial/attribute fields removed.
        Assert.DoesNotContain("data-col-key=\"gmv\"", html);
        Assert.DoesNotContain("data-col-key=\"items\"", html);
        Assert.DoesNotContain("data-col-key=\"sku\"", html);
        Assert.DoesNotContain("data-col-key=\"avgcust\"", html);
        Assert.DoesNotContain("data-col-key=\"ctr\"", html);
        Assert.DoesNotContain("data-col-key=\"hashtags\"", html);
        Assert.DoesNotContain("data-col-key=\"products\"", html);

        // Header cells and body cells for the removed columns.
        Assert.DoesNotContain("data-col=\"gmv\"", html);
        Assert.DoesNotContain("data-col=\"items\"", html);
        Assert.DoesNotContain("data-col=\"sku\"", html);
        Assert.DoesNotContain("data-col=\"avgcust\"", html);
        Assert.DoesNotContain("data-col=\"ctr\"", html);
        Assert.DoesNotContain("data-col=\"hashtags\"", html);
        Assert.DoesNotContain("data-col=\"products\"", html);
        Assert.DoesNotContain("Items Sold", html);
        Assert.DoesNotContain("SKU Orders", html);
    }

    [Fact]
    public async Task Intended_Column_Groups_And_Visibility_Mechanism_Are_Preserved()
    {
        var html = await RenderPageAsync();

        // Remaining group heads intact (7 + 8 + 11 = 26 visible columns).
        Assert.Contains("KONTEN", html);
        Assert.Contains("METRICS", html);
        Assert.Contains("DATA PENONTON (%)", html);
        Assert.Contains("colspan=\"26\"", html);

        // Sample preserved columns across the three groups.
        Assert.Contains("data-col=\"production\"", html);
        Assert.Contains("data-col=\"views\"", html);
        Assert.Contains("data-col=\"reach\"", html);
        Assert.Contains("data-col=\"male\"", html);
        Assert.Contains("data-col=\"country\"", html);

        // localStorage mechanism untouched: same key, same toggle wiring.
        Assert.Contains("contentlog_columns_visibility_v2", html);
        Assert.Contains("class=\"col-toggle\"", html);
    }

    [Fact]
    public async Task Removed_Commercial_Toggles_Cannot_Reappear_Via_LocalStorage_Defaults()
    {
        var html = await RenderPageAsync();

        // The JS builds its defaults map from the rendered .col-toggle checkboxes only, so
        // with the commercial toggles gone, stale localStorage keys for gmv/items/... can
        // never be re-applied to any element: assert no [data-col] node carries them.
        foreach (var key in new[] { "gmv", "items", "sku", "avgcust", "ctr", "hashtags", "products" })
        {
            Assert.DoesNotContain($"data-col=\"{key}\"", html);
            Assert.DoesNotContain($"data-col-key=\"{key}\"", html);
        }
    }

    [Fact]
    public async Task Removed_Rows_Still_Show_Dash_Placeholders_For_Null_Metrics()
    {
        var html = await RenderPageAsync(db =>
        {
            db.ContentLogs.Add(new ContentLog
            {
                VideoId = "cl-null-metrics",
                Title = "Baris tanpa metrik",
                Username = "kaleofficial",
                VideoPostTime = new DateTime(2026, 10, 1, 9, 0, 0)
            });
        });

        Assert.Contains("Baris tanpa metrik", html);
        Assert.Contains(Dash, html); // NULL remains em dash, never 0
    }
}
