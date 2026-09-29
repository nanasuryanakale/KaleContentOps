using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.WinningContent;

/// <summary>
/// Phase 3C below-median UI tests (real MVC pipeline, seeded InMemory store).
/// Mockup layout (visual parity phase): ONE merged panel with a per-type summary table
/// (below / total / % below) and a single ascending full list tagged with each type's
/// median. Business assertions unchanged:
/// - entries render straight from Model.BelowMedian (no UI filtering of its own)
/// - STRICT backend behavior surfaces in the UI: equal-to-median and above-median
///   items never appear
/// - archived Auto GMV Live renders; NULL median renders safe; empty group safe
/// Assertions are culture-independent: titles, codes, counts, structural markers.
/// </summary>
public class WinningContentBelowMedianUiTests
{
    private const string NonKk = "NON_KK";
    private const string Kk = "KK";
    private const string AutoGmv = "AUTO_GMV_LIVE";
    private const string Range = "startDate=2026-09-01&endDate=2026-09-07";

    private static async Task<AuthTestFactory> CreateReadyAsync(string username)
    {
        var factory = new AuthTestFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var (code, name) in new[]
                     {
                         (Kk, "Keranjang Kuning"), (NonKk, "Non-KK"), (AutoGmv, "Auto GMV Live")
                     })
            {
                if (!db.ContentTypes.Any(c => c.Code == code))
                {
                    db.ContentTypes.Add(new ContentType { Code = code, Name = name });
                }
            }
            db.SaveChanges();
        }

        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        return factory;
    }

    /// <summary>Baseline date must be inside [2026-08-02, 2026-09-01) for the fixed period.</summary>
    private static readonly DateTime BaselineDay = new(2026, 8, 20, 12, 0, 0);

    private static void SeedPeriod(AuthTestFactory factory, params (string Title, string Code, bool Archived, long? Views, long? Likes)[] logs)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var typeByCode = db.ContentTypes.ToDictionary(c => c.Code, c => c.Id);
        var day = new DateTime(2026, 9, 3, 12, 0, 0);
        var i = 0;
        foreach (var (title, code, archived, views, likes) in logs)
        {
            var log = new ContentLog
            {
                VideoId = $"bm-{Guid.NewGuid():N}"[..20],
                Title = title,
                Username = "creator",
                VideoUrl = $"https://tiktok.com/@creator/video/{title}",
                VideoPostTime = day.AddHours(i++),
                ContentTypeId = typeByCode[code],
                IsArchived = archived
            };
            db.ContentLogs.Add(log);
            db.ContentMetrics.Add(new ContentMetric
            {
                ContentLog = log,
                Views = views,
                Likes = likes,
                Comments = 0,
                Shares = 0,
                CapturedAt = new DateTime(2026, 9, 8, 1, 0, 0)
            });
        }
        db.SaveChanges();
    }

    private static void SeedBaseline(AuthTestFactory factory, params (string Title, string Code, long Views, long Likes)[] logs)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var typeByCode = db.ContentTypes.ToDictionary(c => c.Code, c => c.Id);
        var i = 0;
        foreach (var (title, code, views, likes) in logs)
        {
            var log = new ContentLog
            {
                VideoId = $"bb-{Guid.NewGuid():N}"[..20],
                Title = title,
                Username = "creator",
                VideoPostTime = BaselineDay.AddHours(i++),
                ContentTypeId = typeByCode[code]
            };
            db.ContentLogs.Add(log);
            db.ContentMetrics.Add(new ContentMetric
            {
                ContentLog = log,
                Views = views,
                Likes = likes,
                Comments = 0,
                Shares = 0,
                CapturedAt = new DateTime(2026, 8, 25, 1, 0, 0)
            });
        }
        db.SaveChanges();
    }

    private static async Task<string> GetPageHtmlAsync(AuthTestFactory factory, string username)
    {
        var client = await factory.SignInAsync(username);
        return await client.GetStringAsync($"/WinningContent?{Range}");
    }

    /// <summary>
    /// Slices the below-median summary row for a category label. The list below the
    /// summary table is merged across types, so type-scoped assertions target the
    /// summary row; title-level assertions run against the whole panel.
    /// </summary>
    private static string BelowChunk(string html, string label)
    {
        var marker = $"wc-below-type type-";
        var start = html.IndexOf(marker + RowTypeFor(label), StringComparison.Ordinal);
        Assert.True(start >= 0, $"below-median summary row '{label}' not found");
        var end = html.IndexOf("</tr>", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    private static string RowTypeFor(string label) => label switch
    {
        "Non-KK" => "nonkk",
        "Keranjang Kuning" => "kk",
        _ => "autogmv"
    };

    /// <summary>Slices the whole below-median panel (summary + list), excluding the
    /// period-items table which also lists content titles.</summary>
    private static string BelowPanel(string html)
    {
        var start = html.IndexOf("wc-below-panel\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "below-median panel not found");
        var end = html.IndexOf("wc-composition\"", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    private static int Count(string html, string needle) =>
        Regex.Matches(html, Regex.Escape(needle)).Count;

    // ------------------------------------------------------------------ structure

    [Fact]
    public async Task BelowMedian_Renders_Three_Independent_Groups_With_Medians()
    {
        const string user = "bm.struct";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // NON_KK period views [2673, 5000, 8371, 10000] -> median 8371.
            SeedPeriod(factory,
                ("BM-NK-A", NonKk, false, 2673, 10),
                ("BM-NK-B", NonKk, false, 5000, 10),
                ("BM-NK-C", NonKk, false, 8371, 10),
                ("BM-NK-D", NonKk, false, 10000, 10));
            // KK period views [2000, 4148, 6000] -> median 4148.
            SeedPeriod(factory,
                ("BM-KK-A", Kk, false, 2000, 10),
                ("BM-KK-B", Kk, false, 4148, 10),
                ("BM-KK-C", Kk, false, 6000, 10));
            // AUTO_GMV_LIVE: [1000, 2000, 3000] -> median 2000.
            SeedPeriod(factory,
                ("BM-AU-A", AutoGmv, false, 1000, 10),
                ("BM-AU-B", AutoGmv, false, 2000, 10),
                ("BM-AU-C", AutoGmv, false, 3000, 10));

            var html = await GetPageHtmlAsync(factory, user);
            var panel = BelowPanel(html);

            // One merged panel: summary table rows per type + one ascending list.
            Assert.Equal(3, Count(html, "wc-below-row"));

            // Median values come from the backend; exact median of an even population is
            // the average of the two middle values, so NON_KK [2673,5000,8371,10000] ->
            // 6.685,5 (display 6.686). Formatted N0 is culture-sensitive, so assert the raw
            // digits in any grouping form (they appear as the per-item median reference in
            // the merged list: NON_KK 6.686, KK 4.148, AUTO 2.000).
            Assert.Matches(@"6[.,\u00a0 ]?68[56]", panel);
            Assert.Matches(@"4[.,\u00a0 ]?148", panel);
            Assert.Matches(@"2[.,\u00a0 ]?000", panel);

            // Summary row: NON_KK 2 below / 4 total / 50% below (2/4 exact display).
            var nonKk = BelowChunk(html, "Non-KK");
            Assert.Contains(">2<", nonKk);
            Assert.Contains(">4<", nonKk);
            Assert.Contains("50%", nonKk);

            // Merged list: only 2,673 and 5,000 are below 8,371 (equal/above excluded).
            Assert.Contains("BM-NK-A", panel);
            Assert.Contains("BM-NK-B", panel);
            Assert.DoesNotContain("BM-NK-C", panel); // equal to median -> NOT displayed
            Assert.DoesNotContain("BM-NK-D", panel); // above median -> NOT displayed
        }
    }

    // ------------------------------------------------------------------ strict boundary

    [Fact]
    public async Task BelowMedian_Strict_Boundary_In_Ui()
    {
        const string user = "bm.strict";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // NON_KK period views [500, 500, 800, 499, 501] -> median 500.
            SeedPeriod(factory,
                ("BM-EQ1", NonKk, false, 500, 5),
                ("BM-EQ2", NonKk, false, 500, 5),
                ("BM-HI", NonKk, false, 800, 5),
                ("BM-LO", NonKk, false, 499, 5),
                ("BM-MID", NonKk, false, 501, 5));

            var html = await GetPageHtmlAsync(factory, user);
            var panel = BelowPanel(html);

            Assert.Contains("BM-LO", panel);        // 499 < 500 -> displayed
            Assert.DoesNotContain("BM-EQ1", panel); // 500 == median -> hidden
            Assert.DoesNotContain("BM-EQ2", panel);
            Assert.DoesNotContain("BM-MID", panel); // 501 > median -> hidden
            Assert.DoesNotContain("BM-HI", panel);
            Assert.Equal(1, Count(panel, "wc-below-item"));
        }
    }

    // ------------------------------------------------------------------ Auto GMV Live

    [Fact]
    public async Task BelowMedian_AutoGmv_Including_Archived_Renders()
    {
        const string user = "bm.auto";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // AUTO_GMV_LIVE: [1000, 2000, 3000, 500(archived)] -> median 2000 (even avg 1500).
            SeedPeriod(factory,
                ("AU-A", AutoGmv, false, 1000, 5),
                ("AU-B", AutoGmv, false, 2000, 5),
                ("AU-C", AutoGmv, false, 3000, 5),
                ("AU-ARCH-LOW", AutoGmv, true, 500, 5));

            var html = await GetPageHtmlAsync(factory, user);
            var panel = BelowPanel(html);

            // Archived below-median entry renders in the merged list (backend output).
            Assert.Contains("AU-ARCH-LOW", panel);
            // [500, 1000, 2000, 3000] -> median (1000+2000)/2 = 1500 (per-item ref).
            Assert.Matches(@"1[.,\u00a0 ]?500", panel);
            // AUTO summary row: 2 below / 4 total / 50%.
            var auto = BelowChunk(html, "Auto GMV Live");
            Assert.Contains(">2<", auto);
            Assert.Contains(">4<", auto);
            Assert.Contains("50%", auto);
        }
    }

    // ------------------------------------------------------------------ null / empty safety

    [Fact]
    public async Task BelowMedian_Null_Median_And_Empty_Category_Are_Safe()
    {
        const string user = "bm.null";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // Only KK has content with valid views; NON_KK has NO content at all
            // (NULL median) and AUTO_GMV_LIVE has a period item with NULL Views
            // (no valid views -> NULL median; the NULL-Views row cannot appear).
            SeedPeriod(factory, ("BM-KK-ONLY", Kk, false, 1000, 5));
            SeedPeriod(factory, ("BM-AU-NULLV", AutoGmv, false, null, null));

            var html = await GetPageHtmlAsync(factory, user);
            var panel = BelowPanel(html);

            // NULL medians surface in the panel note (per type, never as 0).
            Assert.Contains("Median kategori belum tersedia", panel);
            Assert.Contains("belum ada Views valid", panel);
            Assert.DoesNotContain("BM-AU-NULLV", panel); // NULL Views never below-median, never "0"

            // KK summary row: 0 below / 1 total / 0% - valid median, no entries below.
            var kk = BelowChunk(html, "Keranjang Kuning");
            Assert.Contains(">0<", kk);
            Assert.Contains(">1<", kk);
            Assert.Contains("0%", kk);
            Assert.Contains("Tidak ada konten di bawah median", panel);
        }
    }

    // ------------------------------------------------------------------ identity + link

    [Fact]
    public async Task BelowMedian_Row_Shows_Identity_Views_And_Link()
    {
        const string user = "bm.row";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            SeedPeriod(factory,
                ("BM-NK-BIG", NonKk, false, 5000, 5),
                ("BM-NK-SMALL", NonKk, false, 1000, 5));

            var html = await GetPageHtmlAsync(factory, user);
            var panel = BelowPanel(html);

            // Views emphasized as the section's primary metric: views-value class present.
            Assert.Contains("wc-below-views", panel);
            Assert.Matches(@"1[.,\u00a0 ]?000", panel);

            // Identity metadata + accessible video link (merged list, whole panel).
            Assert.Contains("creator", panel);
            Assert.Contains("aria-label=\"Buka video BM-NK-SMALL\"", panel);
            Assert.Contains("href=\"https://tiktok.com/@creator/video/BM-NK-SMALL\"", panel);
        }
    }
}
