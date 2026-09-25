using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.WinningContent;

/// <summary>
/// Phase 3B leaderboard UI tests (real MVC pipeline, seeded InMemory store):
/// - three independent category cards (Non-KK / Keranjang Kuning / Auto GMV Live)
/// - exact Top-N rendering: NON_KK max 4, KK max 4, AUTO_GMV_LIVE max 2
/// - backend order (ER DESC) preserved, ranks rendered from the backend Rank value
/// - NO threshold: sub-1.0x multiplier entries remain visible
/// - archived Auto GMV Live remains visible
/// - NULL multiplier/baseline render as em dash (never 0x / 0%)
/// Assertions avoid culture-formatted decimals; they use titles, rank text and
/// structural class names, which are culture-independent.
/// </summary>
public class WinningContentLeaderboardUiTests
{
    private const string NonKk = "NON_KK";
    private const string Kk = "KK";
    private const string AutoGmv = "AUTO_GMV_LIVE";
    private const string Range = "startDate=2026-09-01&endDate=2026-09-07";

    /// <summary>Factory with content types seeded and a signed-in Viewer client.</summary>
    private static async Task<(AuthTestFactory Factory, HttpClient Client)> CreateReadyAsync()
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

        var username = "wc.ui";
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);
        return (factory, client);
    }

    /// <summary>Seeds period/baseline logs with one metric each. Baseline date is inside [2026-08-02, 2026-09-01).</summary>
    private static void SeedLogs(AuthTestFactory factory, params (string Title, string Code, bool Archived, DateTime PostTime, long Views, long Likes)[] logs)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var typeByCode = db.ContentTypes.ToDictionary(c => c.Code, c => c.Id);
        foreach (var (title, code, archived, postTime, views, likes) in logs)
        {
            var log = new ContentLog
            {
                VideoId = $"ui-{Guid.NewGuid():N}"[..20],
                Title = title,
                Username = "creator",
                VideoUrl = $"https://tiktok.com/@creator/video/{title}",
                VideoPostTime = postTime,
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

    private static async Task<string> GetPageHtmlAsync(HttpClient client) =>
        await client.GetStringAsync($"/WinningContent?{Range}");

    /// <summary>
    /// Slices the leaderboard card chunk for a category label (cards render in the
    /// locked order NON_KK, KK, AUTO_GMV_LIVE - each title appears once per section).
    /// </summary>
    private static string LeaderboardChunk(string html, string label)
    {
        var marker = $"wc-leaderboard-title\">{label}</h3>";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"leaderboard card '{label}' not found");
        start += marker.Length;
        var end = html.IndexOf("wc-leaderboard-title\">", start, StringComparison.Ordinal);
        if (end < 0)
        {
            var sectionEnd = html.IndexOf("wc-below-median\"", start, StringComparison.Ordinal);
            end = sectionEnd < 0 ? html.Length : sectionEnd;
        }
        return html[start..end];
    }

    private static int Count(string html, string needle) =>
        System.Text.RegularExpressions.Regex.Matches(html, System.Text.RegularExpressions.Regex.Escape(needle)).Count;

    // ------------------------------------------------------------------ Top-N rendering

    [Fact]
    public async Task Leaderboard_Renders_Exact_TopN_Per_Category()
    {
        var (factory, client) = await CreateReadyAsync();
        using (factory)
        {
            var period = new DateTime(2026, 9, 3, 12, 0, 0);
            var logs = new System.Collections.Generic.List<(string, string, bool, DateTime, long, long)>();

            // 6 eligible NON_KK (ER 1%..6% via likes 10..60 on 1000 views).
            for (var i = 1; i <= 6; i++)
            {
                logs.Add(($"NKK-{i}", NonKk, false, period.AddDays(-i % 3), 1000, i * 10));
            }
            // 6 eligible KK.
            for (var i = 1; i <= 6; i++)
            {
                logs.Add(($"KK-{i}", Kk, false, period.AddDays(-i % 3), 1000, i * 10));
            }
            // 4 eligible AUTO_GMV_LIVE.
            for (var i = 1; i <= 4; i++)
            {
                logs.Add(($"AUTO-{i}", AutoGmv, false, period.AddDays(-i % 3), 1000, i * 10));
            }
            SeedLogs(factory, logs.ToArray());

            var html = await GetPageHtmlAsync(client);
            var nonKk = LeaderboardChunk(html, "Non-KK");
            var kk = LeaderboardChunk(html, "Keranjang Kuning");
            var auto = LeaderboardChunk(html, "Auto GMV Live");

            // Exact Top-N per card: 4 / 4 / 2.
            Assert.Equal(4, Count(nonKk, "wc-rank-item"));
            Assert.Equal(4, Count(kk, "wc-rank-item"));
            Assert.Equal(2, Count(auto, "wc-rank-item"));

            // Ranks come from the backend value: #1..#4 in NON_KK/KK, #1..#2 only in AUTO.
            Assert.Contains("#1", nonKk);
            Assert.Contains("#4", nonKk);
            Assert.Contains("#4", kk);
            Assert.Contains("#2", auto);
            Assert.DoesNotContain("#3", auto);
            Assert.DoesNotContain("#5", nonKk);

            // 5th/6th NON_KK entries (lowest ERs: likes 10 and 20) are excluded by the
            // backend Top-4 and therefore never rendered.
            Assert.DoesNotContain("NKK-1<", nonKk);
            Assert.DoesNotContain("NKK-2<", nonKk);
        }
    }

    [Fact]
    public async Task Leaderboard_Preserves_Backend_Order_ER_Desc()
    {
        var (factory, client) = await CreateReadyAsync();
        using (factory)
        {
            var period = new DateTime(2026, 9, 3, 12, 0, 0);
            SeedLogs(factory,
                ("NK-LOW", NonKk, false, period, 1000, 10),   // ER 1%  -> rank 3
                ("NK-HIGH", NonKk, false, period, 1000, 50),  // ER 5%  -> rank 1
                ("NK-MID", NonKk, false, period, 1000, 30));  // ER 3%  -> rank 2

            var html = await GetPageHtmlAsync(client);
            var nonKk = LeaderboardChunk(html, "Non-KK");

            var posHigh = nonKk.IndexOf("NK-HIGH", StringComparison.Ordinal);
            var posMid = nonKk.IndexOf("NK-MID", StringComparison.Ordinal);
            var posLow = nonKk.IndexOf("NK-LOW", StringComparison.Ordinal);

            Assert.True(posHigh >= 0 && posMid > posHigh && posLow > posMid,
                "entries must render in backend order ER DESC");
            Assert.Equal(3, Count(nonKk, "wc-rank-item"));
        }
    }

    // ------------------------------------------------------------------ threshold verification

    [Fact]
    public async Task Leaderboard_Shows_Sub1x_Multiplier_Entries_No_Threshold()
    {
        var (factory, client) = await CreateReadyAsync();
        using (factory)
        {
            var period = new DateTime(2026, 9, 3, 12, 0, 0);
            var baseline = new DateTime(2026, 8, 20, 12, 0, 0); // inside [2026-08-02, 2026-09-01)

            // Baseline ER = 5/100 = 0.05 -> multipliers 0.7x / 0.6x / 0.5x / 0.4x.
            SeedLogs(factory,
                ("NK-BASE", NonKk, false, baseline, 100, 5),
                ("NK-SUB07", NonKk, false, period, 1000, 35),
                ("NK-SUB06", NonKk, false, period, 1000, 30),
                ("NK-SUB05", NonKk, false, period, 1000, 25),
                ("NK-SUB04", NonKk, false, period, 1000, 20));

            var html = await GetPageHtmlAsync(client);
            var nonKk = LeaderboardChunk(html, "Non-KK");

            // ALL four entries are below 1.0x and ALL FOUR must be displayed.
            Assert.Contains("NK-SUB07", nonKk);
            Assert.Contains("NK-SUB06", nonKk);
            Assert.Contains("NK-SUB05", nonKk);
            Assert.Contains("NK-SUB04", nonKk);
            Assert.Equal(4, Count(nonKk, "wc-rank-item"));
        }
    }

    [Fact]
    public async Task Leaderboard_Null_Multiplier_Renders_Dash_Never_0x()
    {
        var (factory, client) = await CreateReadyAsync();
        using (factory)
        {
            var period = new DateTime(2026, 9, 3, 12, 0, 0);

            // AUTO_GMV_LIVE: archived period item, NO AUTO baseline videos ->
            // backend multiplier NULL; UI must show it (archived) with a dash.
            SeedLogs(factory,
                ("AUTO-ARCH", AutoGmv, true, period, 500, 5));

            var html = await GetPageHtmlAsync(client);
            var auto = LeaderboardChunk(html, "Auto GMV Live");

            Assert.Contains("AUTO-ARCH", auto);                 // archived entry visible
            // NULL multiplier/baseline render as an em dash placeholder (any HTML encoding form).
            var hasDash = auto.Contains("&#8212;")
                || auto.Contains("&#x2014;")
                || auto.Contains('\u2014');
            Assert.True(hasDash, "NULL values must render as an em dash placeholder");
            Assert.DoesNotContain("0x", auto);                  // never rendered as 0x
            Assert.Equal(1, Count(auto, "wc-rank-item"));
        }
    }

    // ------------------------------------------------------------------ structure / a11y

    [Fact]
    public async Task Leaderboard_Cards_Are_Independent_And_Linked()
    {
        var (factory, client) = await CreateReadyAsync();
        using (factory)
        {
            var period = new DateTime(2026, 9, 3, 12, 0, 0);
            SeedLogs(factory,
                ("NK-ITEM", NonKk, false, period, 1000, 50),
                ("KK-ITEM", Kk, false, period, 1000, 40),
                ("AUTO-ITEM", AutoGmv, false, period, 1000, 30));

            var html = await GetPageHtmlAsync(client);

            // Exactly three independent cards.
            Assert.Equal(3, Count(html, "wc-leaderboard-title"));
            Assert.Contains("Top 4 ER", html);
            Assert.Contains("Top 2 ER", html);

            // Video link renders with an accessible label.
            Assert.Contains("aria-label=\"Buka video NK-ITEM\"", html);
            Assert.Contains("href=\"https://tiktok.com/@creator/video/NK-ITEM\"", html);

            // Rank badges carry accessible labels.
            Assert.Contains("aria-label=\"Peringkat 1\"", html);
        }
    }

    [Fact]
    public async Task Sidebar_Active_State_Remains_WinningContent_Only()
    {
        var (factory, client) = await CreateReadyAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            Assert.Equal(1, Count(html, "nav-link active"));
            Assert.Contains("/WinningContent", html);
        }
    }
}
