using System;
using System.Collections.Generic;
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
/// Phase 3D composition UI tests (real MVC pipeline, seeded InMemory store):
/// - SVG donut rendered straight from Model.CompositionSummary (backend order and values)
/// - legend shows all three locked category slots, zero-count categories included
/// - arc lengths are exact backend percentages (no rounding of stored percentages)
/// - no-data period renders the safe empty state (no donut, no fabricated split)
/// - zero-count category renders 0% and no arc; archived Auto GMV Live still counts
/// - composition is independent of Views (count-based, not views-based)
/// - mockup sample 10/4/1: exact arc geometry 175.93/70.37/17.59 of C=2*pi*42, display 67/27/7
/// Assertions are culture-independent: codes, titles, counts, structural markers.
/// </summary>
public class WinningContentCompositionUiTests
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
                VideoId = $"cmp-{Guid.NewGuid():N}"[..20],
                Title = title,
                Username = "creator",
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

    private static async Task<string> GetPageHtmlAsync(AuthTestFactory factory, string username)
    {
        var client = await factory.SignInAsync(username);
        return await client.GetStringAsync($"/WinningContent?{Range}");
    }

    /// <summary>Slices the composition section out of the page HTML.</summary>
    private static string CompositionChunk(string html)
    {
        var start = html.IndexOf("wc-composition\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "composition section not found");
        var end = html.IndexOf("wc-items", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    private static int Count(string html, string needle) =>
        Regex.Matches(html, Regex.Escape(needle)).Count;

    /// <summary>Slices only the legend list (section notes also mention category names).</summary>
    private static string LegendChunk(string html)
    {
        var start = html.IndexOf("wc-legend\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "legend not found");
        var end = html.IndexOf("</ul>", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    private static readonly Regex ArcRegex = new(
        "stroke-dasharray=\"(?<len>[0-9.]+) (?<gap>[0-9.]+)\"\\s*stroke-dashoffset=\"(?<off>-?[0-9.]+)\"",
        RegexOptions.Compiled);

    /// <summary>Parses every donut arc's dasharray/dashoffset (SVG numbers, invariant culture).</summary>
    private static List<(decimal Length, decimal Gap, decimal Offset)> ArcDashArrays(string html)
    {
        var arcs = new List<(decimal, decimal, decimal)>();
        foreach (Match match in ArcRegex.Matches(html))
        {
            arcs.Add(
                (decimal.Parse(match.Groups["len"].Value, System.Globalization.CultureInfo.InvariantCulture),
                 decimal.Parse(match.Groups["gap"].Value, System.Globalization.CultureInfo.InvariantCulture),
                 decimal.Parse(match.Groups["off"].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }
        return arcs;
    }

    // ------------------------------------------------------------------ structure

    [Fact]
    public async Task Composition_Renders_Donut_And_Three_Slot_Legend()
    {
        const string user = "cmp.struct";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // NON_KK 10 / KK 4 / AUTO 1 -> mockup sample 10-4-1.
            for (var i = 0; i < 10; i++) SeedPeriod(factory, ($"CMP-NK-{i}", NonKk, false, 1000, 10));
            for (var i = 0; i < 4; i++) SeedPeriod(factory, ($"CMP-KK-{i}", Kk, false, 1000, 10));
            SeedPeriod(factory, ("CMP-AU", AutoGmv, false, 1000, 10));

            var html = await GetPageHtmlAsync(factory, user);
            var chunk = CompositionChunk(html);

            // Donut + track + mockup panel subtitle (KOMPOSISI JENIS KONTEN).
            Assert.Contains("wc-donut-track", chunk);
            Assert.Contains("wc-comp-subtitle", chunk);
            Assert.Contains("KOMPOSISI JENIS KONTEN", chunk);

            // One arc per non-zero category (3 arcs for 10-4-1), exact backend-proportion
            // geometry: C = 2*pi*42 = 263.8937...; dash length = exact % x C rounded to 2 dp
            // (66.66..% -> 175.93, 26.66..% -> 70.37, 6.66..% -> 17.59). Arcs accumulate in
            // locked order starting at 12 o'clock (negative dashoffset = rotation).
            Assert.Equal(3, Count(chunk, "wc-donut-arc"));
            Assert.Collection(
                ArcDashArrays(chunk),
                arc =>
                {
                    Assert.Equal(175.93m, arc.Length);
                    Assert.Equal(0m, arc.Offset);
                },
                arc =>
                {
                    Assert.Equal(70.37m, arc.Length);
                    Assert.Equal(-175.93m, arc.Offset);
                },
                arc =>
                {
                    Assert.Equal(17.59m, arc.Length);
                    Assert.Equal(-246.3m, arc.Offset);
                });

            // Legend: all three locked slots always present, in locked order
            // (labels only - the UI never renders raw content-type codes).
            var legend = LegendChunk(html);
            Assert.Equal(3, Count(chunk, "wc-legend-item"));
            Assert.True(legend.IndexOf("Non-KK", StringComparison.Ordinal) < legend.IndexOf("Keranjang Kuning", StringComparison.Ordinal));
            Assert.True(legend.IndexOf("Keranjang Kuning", StringComparison.Ordinal) < legend.IndexOf("Auto GMV Live", StringComparison.Ordinal));

            // Mockup display convention: integer half-up rounding of the exact backend
            // percentages (66.66.. -> 67, 26.66.. -> 27, 6.66.. -> 7). Rounded labels sum
            // to 101 here - accepted presentation drift; the chart itself uses exact values.
            Assert.Contains("67%", chunk);
            Assert.Contains("27%", chunk);
            Assert.Contains("7%", chunk);
        }
    }

    // ------------------------------------------------------------------ values

    [Fact]
    public async Task Composition_Is_Independent_Of_Views()
    {
        const string user = "cmp.views";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // NON_KK: 1 video with 1,000,000 Views; KK: 9 videos with 100 Views each; AUTO: 0.
            // Composition counts CONTENT, not views -> expected 10% / 90% / 0%.
            SeedPeriod(factory, ("CMP-W-NK1", NonKk, false, 1_000_000, 10));
            for (var i = 0; i < 9; i++) SeedPeriod(factory, ($"CMP-W-KK{i}", Kk, false, 100, 10));

            var html = await GetPageHtmlAsync(factory, user);
            var chunk = CompositionChunk(html);

            // Legend: percentages by COUNT (1/10, 9/10, 0/10) - never by views.
            Assert.Contains("10%", chunk);
            Assert.Contains("90%", chunk);
            Assert.Contains("0%", chunk);

            // Arcs: exactly 2 (AUTO has zero count -> no arc, stays in legend),
            // geometry from exact percentages: 10% -> 26.39, 90% -> 237.5 of C=263.8937...
            Assert.Equal(2, Count(chunk, "wc-donut-arc"));
            Assert.Equal(3, Count(chunk, "wc-legend-item"));
            Assert.Collection(
                ArcDashArrays(chunk),
                arc =>
                {
                    Assert.Equal(26.39m, arc.Length);
                    Assert.Equal(0m, arc.Offset);
                },
                arc =>
                {
                    Assert.Equal(237.5m, arc.Length);
                    Assert.Equal(-26.39m, arc.Offset);
                });
        }
    }

    // ------------------------------------------------------------------ donut math

    [Fact]
    public async Task Composition_Single_Category_Renders_Full_Circle()
    {
        const string user = "cmp.single";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // NON_KK 10 / KK 0 / AUTO 0 -> single category, full-circle arc, others 0%.
            for (var i = 0; i < 10; i++) SeedPeriod(factory, ($"CMP-S-NK{i}", NonKk, false, 1000, 10));

            var html = await GetPageHtmlAsync(factory, user);
            var chunk = CompositionChunk(html);

            Assert.Equal(1, Count(chunk, "wc-donut-arc"));    // KK and AUTO draw nothing
            Assert.Equal(3, Count(chunk, "wc-legend-item")); // but stay in the legend
            Assert.Contains("100%", chunk);
            Assert.Contains("0%", chunk);

            // Arc dasharray reflects the full circumference for a 100% share.
            // r=42 -> C = 2*pi*42 = 263.893...; 100% -> dasharray "263.89 0".
            var arc = Assert.Single(ArcDashArrays(chunk));
            Assert.Equal(263.89m, arc.Length);
            Assert.Equal(0m, arc.Gap);
        }
    }

    // ------------------------------------------------------------------ no data

    [Fact]
    public async Task Composition_NoData_Period_Renders_Safe_State()
    {
        const string user = "cmp.empty";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // Content exists but OUTSIDE the selected period (period = Sep 01..07).
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var nonKkId = db.ContentTypes.Single(c => c.Code == NonKk).Id;
                var log = new ContentLog
                {
                    VideoId = $"cmp-out-{Guid.NewGuid():N}"[..20],
                    Title = "CMP-OUT",
                    Username = "creator",
                    VideoPostTime = new DateTime(2026, 9, 20, 12, 0, 0),  // after period end
                    ContentTypeId = nonKkId
                };
                db.ContentLogs.Add(log);
                db.ContentMetrics.Add(new ContentMetric
                {
                    ContentLog = log,
                    Views = 1000,
                    Likes = 10,
                    Comments = 0,
                    Shares = 0,
                    CapturedAt = new DateTime(2026, 9, 21, 1, 0, 0)
                });
                db.SaveChanges();
            }

            var html = await GetPageHtmlAsync(factory, user);
            var chunk = CompositionChunk(html);

            Assert.Contains("Tidak ada konten pada periode yang dipilih", chunk);
            Assert.DoesNotContain("wc-donut", chunk);   // no fabricated chart
            Assert.DoesNotContain("wc-legend-item", chunk); // no fabricated split
        }
    }

    // ------------------------------------------------------------------ zero category

    [Fact]
    public async Task Composition_Zero_Category_Renders_Zero_In_Legend_No_Arc()
    {
        const string user = "cmp.zero";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // NON_KK 10 / KK 0 / AUTO 1 -> total 11; KK slot stays with 0% and no arc.
            for (var i = 0; i < 10; i++) SeedPeriod(factory, ($"CMP-Z-NK{i}", NonKk, false, 1000, 10));
            SeedPeriod(factory, ("CMP-Z-AU", AutoGmv, false, 1000, 10));

            var html = await GetPageHtmlAsync(factory, user);
            var chunk = CompositionChunk(html);

            Assert.Equal(2, Count(chunk, "wc-donut-arc"));   // only NON_KK + AUTO arcs
            Assert.Equal(3, Count(chunk, "wc-legend-item")); // KK slot still present
            Assert.Contains("0%", chunk);                    // KK rendered as exactly 0%
            Assert.Contains("91%", chunk);                   // 10/11 = 90.909.. displays 91
        }
    }

    // ------------------------------------------------------------------ archived auto gmv

    [Fact]
    public async Task Composition_Archived_AutoGmv_Counts_In_Chart()
    {
        const string user = "cmp.arch";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            // NON_KK 10 / KK 4 / AUTO 1 (archived) -> same 10-4-1 split as the mockup.
            for (var i = 0; i < 10; i++) SeedPeriod(factory, ($"CMP-AR-NK{i}", NonKk, false, 1000, 10));
            for (var i = 0; i < 4; i++) SeedPeriod(factory, ($"CMP-AR-KK{i}", Kk, false, 1000, 10));
            SeedPeriod(factory, ("CMP-AR-ARCH", AutoGmv, true, 1000, 10));

            var html = await GetPageHtmlAsync(factory, user);
            var chunk = CompositionChunk(html);

            // Archived Auto GMV Live participates: three arcs, AUTO slot present.
            Assert.Equal(3, Count(chunk, "wc-donut-arc"));
            Assert.Contains("Auto GMV Live", chunk);   // legend label (codes are never rendered)
        }
    }
}
