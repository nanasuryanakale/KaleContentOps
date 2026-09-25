using System;
using System.Linq;
using System.Net;
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
/// Phase 3A end-to-end page tests through the real MVC pipeline (AuthTestFactory,
/// per-factory InMemory store): /WinningContent requires authentication, renders REAL
/// backend data (leaderboards, medians, below median, composition), empty/NULL states
/// render safely, and the sidebar highlights Winning Content only on this route.
/// Values asserted in HTML are culture-independent (titles, counts, codes) - formatted
/// decimals (ER%, medians) are intentionally not asserted because the test host
/// culture may vary; numeric correctness is covered by the Phase 2A-2D suites.
/// </summary>
public class WinningContentPageIntegrationTests
{
    private const string NonKk = "NON_KK";
    private const string Kk = "KK";
    private const string AutoGmv = "AUTO_GMV_LIVE";

    private const string FixedStart = "2026-09-01";
    private const string FixedEnd = "2026-09-07";

    /// <summary>Creates a factory with deterministic seeded content and a signed-in client.</summary>
    private static async Task<(AuthTestFactory Factory, HttpClient Client)> CreateSeededAsync()
    {
        var factory = new AuthTestFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Idempotent content-type seeding (works with or without model seed data).
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

            var typeByCode = db.ContentTypes.ToDictionary(c => c.Code, c => c.Id);

            ContentLog Log(string videoId, string title, string code, DateTime postTime, long views, long likes)
            {
                var log = new ContentLog
                {
                    VideoId = videoId,
                    Title = title,
                    Username = "creator",
                    VideoUrl = $"https://tiktok.com/@creator/video/{videoId}",
                    VideoPostTime = postTime,
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
                    CapturedAt = new DateTime(2026, 9, 8, 1, 0, 0)
                });
                return log;
            }

            // NON_KK: ERs 0.05 / 0.03 / 0.01; Views [1000, 1000, 300] -> median 1000,
            // below-median = {300}. KK: 1 item. AUTO_GMV_LIVE: 1 item.
            Log("wc-top", "WC Top Winner", NonKk, new DateTime(2026, 9, 2, 12, 0, 0), 1000, 50);
            Log("wc-second", "WC Second", NonKk, new DateTime(2026, 9, 3, 12, 0, 0), 1000, 30);
            Log("wc-low", "WC Low Views", NonKk, new DateTime(2026, 9, 4, 12, 0, 0), 300, 3);
            Log("wc-kk", "WC KK Item", Kk, new DateTime(2026, 9, 5, 12, 0, 0), 200, 2);
            Log("wc-auto", "WC AutoGMV Live", AutoGmv, new DateTime(2026, 9, 6, 12, 0, 0), 500, 5);
            db.SaveChanges();
        }

        var username = "wc.page";
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);
        return (factory, client);
    }

    private static async Task<string> GetPageHtmlAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"/WinningContent?startDate={FixedStart}&endDate={FixedEnd}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Returns the class attribute of the anchor whose href ends with the given path.</summary>
    private static string? AnchorClassBefore(string html, string href)
    {
        var idx = html.IndexOf(href, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }
        var from = Math.Max(0, idx - 300);
        return html[from..idx];
    }

    [Fact]
    public async Task Anonymous_Request_Redirects_To_Login()
    {
        using var factory = new AuthTestFactory();
        var client = factory.CreateBrowserClient(allowAutoRedirect: false);

        var response = await client.GetAsync("/WinningContent");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Page_Renders_Real_Backend_Data()
    {
        var (factory, client) = await CreateSeededAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // Page structure.
            Assert.Contains("Winning Content", html);
            Assert.Contains("Leaderboard", html);
            Assert.Contains("Distribusi di Bawah Median", html);
            Assert.Contains("Komposisi Konten", html);

            // Date-range binding: the selected range is echoed into the inputs.
            Assert.Contains($"value=\"{FixedStart}\"", html);
            Assert.Contains($"value=\"{FixedEnd}\"", html);

            // Real content titles from the backend model (not hard-coded samples).
            Assert.Contains("WC Top Winner", html);
            Assert.Contains("WC Second", html);
            Assert.Contains("WC Low Views", html);
            Assert.Contains("WC KK Item", html);
            Assert.Contains("WC AutoGMV Live", html);

            // Leaderboard foot with baseline (all three categories render a card).
            Assert.Contains("Baseline ER kategori", html);

            // Composition: NON_KK 3 / KK 1 / AUTO 1 -> total 5, NON_KK 60% exact.
            Assert.Contains("5</strong> konten", html);
            Assert.Contains("60%", html);

            // All three category labels present.
            Assert.Contains("Non-KK", html);
            Assert.Contains("Keranjang Kuning", html);
            Assert.Contains("Auto GMV Live", html);
        }
    }

    [Fact]
    public async Task Empty_Period_Renders_NoData_States_Without_Crashing()
    {
        using var factory = new AuthTestFactory();
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

        var username = "wc.empty";
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);

        using var response = await client.GetAsync($"/WinningContent?startDate={FixedStart}&endDate={FixedEnd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // no exception on empty data
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Tidak ada konten pada periode yang dipilih", html);
        Assert.Contains("Belum ada konten dengan Engagement Rate", html);
        Assert.Contains("Median kategori belum tersedia", html);
    }

    [Fact]
    public async Task Sidebar_Activates_WinningContent_Only()
    {
        var (factory, client) = await CreateSeededAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // The Winning Content link is wired to the real route and is active.
            var wcChunk = AnchorClassBefore(html, "/WinningContent");
            Assert.NotNull(wcChunk);
            Assert.Contains("nav-link active", wcChunk);

            // Other menus stay inactive on this page.
            var dailyChunk = AnchorClassBefore(html, "/DailySummary");
            Assert.NotNull(dailyChunk);
            Assert.DoesNotContain("nav-link active", dailyChunk);

            // Exactly one active menu item on the page.
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, "nav-link active").Count);
        }
    }
}
