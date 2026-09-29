using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Services;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.WinningContent;

/// <summary>
/// Phase 3E.1 date-range UI parity tests (real MVC pipeline, seeded InMemory store).
/// Winning Content now uses the Daily Summary date-range pattern: a calendar range
/// button (dd/MM label + GMT+7 note), a popover panel with quick presets and the
/// shared two-month calendar (wwwroot/js/calendar-popup.js), and Apply navigation.
/// These tests pin the structural parity with the Daily Summary reference page and
/// the server-authoritative date semantics (default 7-day window, exact query-string
/// binding, invalid-input safety). Visual parity itself is verified in the browser.
/// </summary>
public class WinningContentDateRangeUiTests
{
    private const string Range = "startDate=2026-09-01&endDate=2026-09-07";

    private static async Task<AuthTestFactory> CreateReadyAsync(string username)
    {
        var factory = new AuthTestFactory();
        using (var scope = factory.Services.CreateScope())
        {
            // WinningContentService requires the locked three-category catalog
            // (same seeding contract as the Phase 3B/3C/3D UI tests).
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var (code, name) in new[]
                     {
                         ("KK", "Keranjang Kuning"), ("NON_KK", "Non-KK"), ("AUTO_GMV_LIVE", "Auto GMV Live")
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

    private static async Task<string> GetStringAsync(AuthTestFactory factory, string username, string path)
    {
        var client = await factory.SignInAsync(username);
        return await client.GetStringAsync(path);
    }

    /// <summary>Reads the applied range the page exposes for the picker (data attributes).</summary>
    private static (DateTime Start, DateTime End) AppliedRange(string html)
    {
        var match = Regex.Match(html, "data-start=\"(\\d{4}-\\d{2}-\\d{2})\" data-end=\"(\\d{4}-\\d{2}-\\d{2})\"");
        Assert.True(match.Success, "applied range data attributes not found");
        return (DateTime.Parse(match.Groups[1].Value), DateTime.Parse(match.Groups[2].Value));
    }

    private static void SeedContent(AuthTestFactory factory, string title, string code, long views, long likes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var typeId = db.ContentTypes.Single(c => c.Code == code).Id;
        var log = new ContentLog
        {
            VideoId = $"dr-{Guid.NewGuid():N}"[..20],
            Title = title,
            Username = "creator",
            VideoPostTime = new DateTime(2026, 9, 3, 12, 0, 0),   // inside Sep 01-07
            ContentTypeId = typeId
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
        db.SaveChanges();
    }

    // ------------------------------------------------------------------ rendering

    [Fact]
    public async Task DateRange_Renders_DailySummary_Pattern()
    {
        const string user = "dr.pattern";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var html = await GetStringAsync(factory, user, $"/WinningContent?{Range}");

            // Trigger button: Daily Summary structure (calendar icon + dd/MM label + GMT+7 note).
            Assert.Contains("class=\"date-range-button\"", html);
            Assert.Contains("id=\"dateRangeButton\"", html);
            Assert.Contains("aria-expanded=\"false\"", html);
            Assert.Contains("aria-controls=\"datePickerPanel\"", html);
            Assert.Contains("GMT+7", html);
            Assert.Contains("<strong id=\"dateRangeLabel\">01/09 - 07/09</strong>", html);

            // Popover panel with the Daily Summary structure: topline, presets,
            // month navigation, shared calendar grid, footer + Apply/Cancel.
            Assert.Contains("id=\"datePickerPanel\" hidden", html);   // starts closed like DS
            Assert.Contains("PERIODE CEPAT", html);
            Assert.Contains("data-preset=\"today\"", html);
            Assert.Contains("data-preset=\"yesterday\"", html);
            Assert.Contains("data-preset=\"7\"", html);
            Assert.Contains("data-preset=\"30\"", html);
            Assert.Contains("data-preset=\"90\"", html);
            Assert.Contains("id=\"previousMonth\"", html);
            Assert.Contains("id=\"nextMonth\"", html);
            Assert.Contains("id=\"calendarGrid\"", html);
            Assert.Contains("id=\"datePickerSelected\"", html);
            Assert.Contains("id=\"applyDatePicker\"", html);
            Assert.Contains("id=\"cancelDatePicker\"", html);

            // Same shared calendar assets as Daily Summary.
            Assert.Contains("js/calendar-popup.js", html);
            Assert.Contains("css/calendar-popup.css", html);

            // Apply submits the existing controller contract (server-authoritative dates).
            Assert.Contains("/WinningContent?startDate=", html);

            // The old native date form is fully replaced.
            Assert.DoesNotContain("wc-date-range", html);
            Assert.DoesNotContain("type=\"date\"", html);
        }
    }

    [Fact]
    public async Task DateRange_Structure_Matches_DailySummary_Reference()
    {
        const string user = "dr.parity";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var winningContent = await GetStringAsync(factory, user, $"/WinningContent?{Range}");
            var dailySummary = await GetStringAsync(factory, user, "/DailySummary");

            // The Daily Summary date-range component contract is present on both pages:
            // same classes, same element ids, same shared module/CSS, same presets.
            string[] markers =
            {
                "class=\"date-range-button\"", "id=\"dateRangeButton\"", "id=\"datePickerPanel\"",
                "class=\"preset-list\"", "PERIODE CEPAT", "data-preset=\"7\"", "data-preset=\"90\"",
                "id=\"calendarGrid\"", "id=\"applyDatePicker\"", "id=\"cancelDatePicker\"",
                "js/calendar-popup.js", "css/calendar-popup.css", "date-picker-panel",
                "GMT+7"
            };
            foreach (var marker in markers)
            {
                Assert.Contains(marker, winningContent);
                Assert.Contains(marker, dailySummary);
            }

            // Both pages expose the applied range the same way (data-start/data-end).
            Assert.Contains("data-start=\"2026-09-01\"", winningContent);
            Assert.Contains("data-end=\"2026-09-07\"", winningContent);
        }
    }

    // ------------------------------------------------------------------ semantics

    [Fact]
    public async Task DateRange_Default_SevenDay_Window_Preserved()
    {
        const string user = "dr.default";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            string html;
            using (var scope = factory.Services.CreateScope())
            {
                // Expectation comes from the SAME timezone infrastructure the controller
                // uses (IShopTimeZone -> Asia/Jakarta today) - no hard-coded calendar date.
                var timeZone = scope.ServiceProvider.GetRequiredService<IShopTimeZone>();
                var expectedEnd = timeZone.TodayMidnight().Date;

                html = await GetStringAsync(factory, user, "/WinningContent");

                var (start, end) = AppliedRange(html);
                Assert.Equal(expectedEnd, end);                    // shop-local today
                Assert.Equal(expectedEnd.AddDays(-6), start);      // 7 calendar days inclusive
            }
        }
    }

    [Fact]
    public async Task DateRange_QueryString_Dates_Bind_Exactly()
    {
        const string user = "dr.bind";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var html = await GetStringAsync(factory, user, $"/WinningContent?{Range}");

            // The picker initializes to the applied (query-string) range...
            var (start, end) = AppliedRange(html);
            Assert.Equal(new DateTime(2026, 9, 1), start);
            Assert.Equal(new DateTime(2026, 9, 7), end);

            // ...and the button label shows it in the Daily Summary display format (dd/MM).
            Assert.Contains("01/09 - 07/09", html);
        }
    }

    [Fact]
    public async Task DateRange_Invalid_Query_Dates_Fall_Back_To_Defaults()
    {
        const string user = "dr.invalid";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var client = await factory.SignInAsync(user);
            var response = await client.GetAsync("/WinningContent?startDate=not-a-date&endDate=42");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();

            using (var scope = factory.Services.CreateScope())
            {
                var timeZone = scope.ServiceProvider.GetRequiredService<IShopTimeZone>();
                var expectedEnd = timeZone.TodayMidnight().Date;
                var (start, end) = AppliedRange(html);
                Assert.Equal(expectedEnd, end);
                Assert.Equal(expectedEnd.AddDays(-6), start);
            }

            // The page still renders every section (no error page).
            Assert.Contains("wc-leaderboard-grid", html);
        }
    }

    // ------------------------------------------------------------------ regression

    [Fact]
    public async Task DateRange_Rerender_Keeps_All_WinningContent_Sections()
    {            const string user = "dr.sections";
            var factory = await CreateReadyAsync(user);
            using (factory)
            {
                // In-period content so every section renders with data (like the real page).
                SeedContent(factory, "DR-NK-1", "NON_KK", 1000, 10);
                SeedContent(factory, "DR-NK-2", "NON_KK", 500, 5);

                var html = await GetStringAsync(factory, user, $"/WinningContent?{Range}");

            // Phase 3B/3C/3D sections still render alongside the new header
            // (mockup headings: numbered sections).
            Assert.Contains("wc-leaderboard-grid", html);
            Assert.Contains("wc-below-panel", html);
            Assert.Contains("wc-composition-card", html);
            Assert.Contains("wc-donut", html);
            Assert.Contains("wc-legend-item", html);
            Assert.Contains("Komposisi Konten", html);
            Assert.Contains("Distribusi Konten di Bawah Median", html);
            Assert.Contains("Leaderboard per Jenis Konten", html);
        }
    }

    [Fact]
    public async Task Sidebar_Active_State_Remains_WinningContent()
    {
        const string user = "dr.sidebar";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var html = await GetStringAsync(factory, user, $"/WinningContent?{Range}");
            Assert.Contains("class=\"nav-link active\" href=\"/WinningContent\"", html);
            Assert.DoesNotContain("class=\"nav-link active\" href=\"/DailySummary\"", html);
        }
    }
}
