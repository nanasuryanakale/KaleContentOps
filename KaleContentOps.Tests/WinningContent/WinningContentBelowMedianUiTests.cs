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
/// Phase 3C below-median UI tests (real MVC pipeline, seeded InMemory store):
/// - three independent category groups, each with its own median badge
/// - below-median entries render straight from Model.BelowMedian (backend order)
/// - STRICT backend behavior surfaces in the UI: equal-to-median and above-median
///   items never appear (the UI adds no filtering of its own)
/// - mockup sample: NON_KK median 8,371 -> 2,673 and 5,000 visible, 8,371 not
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

    /// <summary>Slices the below-median group card for a category label.</summary>
    private static string BelowChunk(string html, string label)
    {
        var marker = $"wc-below-title\">{label}</h3>";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"below-median group '{label}' not found");
        start += marker.Length;
        var end = html.IndexOf("wc-below-title\">", start, StringComparison.Ordinal);
        if (end < 0)
        {
            var sectionEnd = html.IndexOf("wc-composition\"", start, StringComparison.Ordinal);
            end = sectionEnd < 0 ? html.Length : sectionEnd;
        }
        return html[start..end];
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

            // Three independent group cards.
            Assert.Equal(3, Count(html, "wc-below-group"));

            // Median values come from the backend; 8,371 formatted N0 is culture-sensitive,
            // so assert the raw digits in any grouping form.
            Assert.Matches(@"8[.,\u00a0 ]?371", html);
            Assert.Matches(@"4[.,\u00a0 ]?148", html);
            Assert.Matches(@"2[.,\u00a0 ]?000", html);

            // NON_KK group: only 2,673 and 5,000 are below 8,371 (equal 8,371 excluded).
            var nonKk = BelowChunk(html, "Non-KK");
            Assert.Contains("BM-NK-A", nonKk);
            Assert.Contains("BM-NK-B", nonKk);
            Assert.DoesNotContain("BM-NK-C", nonKk); // equal to median -> NOT displayed
            Assert.DoesNotContain("BM-NK-D", nonKk); // above median -> NOT displayed
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
            var nonKk = BelowChunk(html, "Non-KK");

            Assert.Contains("BM-LO", nonKk);        // 499 < 500 -> displayed
            Assert.DoesNotContain("BM-EQ1", nonKk); // 500 == median -> hidden
            Assert.DoesNotContain("BM-EQ2", nonKk);
            Assert.DoesNotContain("BM-MID", nonKk); // 501 > median -> hidden
            Assert.DoesNotContain("BM-HI", nonKk);
            Assert.Equal(1, Count(nonKk, "wc-below-item"));
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
            var auto = BelowChunk(html, "Auto GMV Live");

            // Archived below-median entry renders exactly as returned by the backend.
            Assert.Contains("AU-ARCH-LOW", auto);
            // [500, 1000, 2000, 3000] -> median (1000+2000)/2 = 1500.
            Assert.Matches(@"1[.,\u00a0 ]?500", html);
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

            var nonKk = BelowChunk(html, "Non-KK");
            Assert.Contains("Median kategori belum tersedia", nonKk);

            var auto = BelowChunk(html, "Auto GMV Live");
            Assert.Contains("Median kategori belum tersedia", auto);
            Assert.DoesNotContain("BM-AU-NULLV", auto); // NULL Views never below-median, never "0"

            var kk = BelowChunk(html, "Keranjang Kuning");
            Assert.Contains("Tidak ada konten di bawah median", kk); // valid median, no entries below
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
            var nonKk = BelowChunk(html, "Non-KK");

            // Views emphasized as the section's primary metric: views-value class present.
            Assert.Contains("wc-below-views", nonKk);
            Assert.Matches(@"1[.,\u00a0 ]?000", nonKk);

            // Identity metadata + accessible video link.
            Assert.Contains("creator", nonKk);
            Assert.Contains("aria-label=\"Buka video BM-NK-SMALL\"", nonKk);
            Assert.Contains("href=\"https://tiktok.com/@creator/video/BM-NK-SMALL\"", nonKk);
        }
    }
}
