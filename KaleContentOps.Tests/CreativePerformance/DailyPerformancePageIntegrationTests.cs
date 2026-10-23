using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using KaleContentOps.Tests;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// End-to-end page tests through the real MVC pipeline (AuthTestFactory): the
/// /DailyPerformance route resolves, DI supplies the registered account-level
/// source, the page renders the mockup sections with the BLOCKED (unavailable)
/// provider state - no fabricated zeros - and the sidebar highlights Daily only
/// on this route. Numeric correctness is covered by the service/calculator suites.
/// </summary>
public class DailyPerformancePageIntegrationTests
{
    private const string FixedStart = "2026-08-23";
    private const string FixedEnd = "2026-09-05";

    private static async Task<(AuthTestFactory Factory, HttpClient Client)> CreateSignedInAsync()
    {
        var factory = new AuthTestFactory();
        var username = "cpd.page";
        await factory.CreateRoleUserAsync(username, Security.AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);
        return (factory, client);
    }

    private static async Task<string> GetPageHtmlAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"/DailyPerformance?startDate={FixedStart}&endDate={FixedEnd}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Anonymous_Request_Redirects_To_Login()
    {
        using var factory = new AuthTestFactory();
        var client = factory.CreateBrowserClient(allowAutoRedirect: false);

        using var response = await client.GetAsync("/DailyPerformance");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Page_Renders_AllMockupSections()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // Header + date picker contract.
            Assert.Contains("Creative Performance", html);
            Assert.Contains("Daily Report", html);
            Assert.Contains("dateRangeButton", html);
            Assert.Contains("23/08", html);
            Assert.Contains("05/09", html);

            // Seven summary cards.
            foreach (var label in new[] { "REACH", "VIDEO VIEWS", "PROFILE VIEWS", "INTERACTIONS", "ENGAGEMENT RATE", "NET FOLLOWER", "TOTAL FOLLOWER" })
            {
                Assert.Contains(label, html);
            }

            // Period comparison table + both charts + both donuts + daily table.
            Assert.Contains("PERBANDINGAN PERIODE", html);
            Assert.Contains("TREN REACH", html);
            Assert.Contains("PERGERAKAN NET FOLLOWER", html);
            Assert.Contains("KOMPOSISI INTERACTIONS", html);
            Assert.Contains("VIEWER BARU VS LAMA", html);
            Assert.Contains("DETAIL HARIAN", html);

            // Removed per mockup (2026-10-23): the shop-video commerce sections and the
            // metric-definitions footer no longer exist on this page - and no empty
            // container of them may linger either.
            Assert.DoesNotContain("METRIK SHOP VIDEO", html);
            Assert.DoesNotContain("DETAIL SHOP VIDEO HARIAN", html);
            Assert.DoesNotContain("Definisi metrik", html);
            Assert.DoesNotContain("cpd-shop-video", html);
            Assert.DoesNotContain("cpd-shop-daily", html);
            Assert.DoesNotContain("cpd-footer", html);

            // Chart payload travels as embedded JSON for the client renderer.
            Assert.Contains("cpdChartPayload", html);
        }
    }

    [Fact]
    public async Task Page_ShowsBlockedSourceNotice_AndDashes_NotFakeZeros()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // The unavailable provider's honest notice must be on the page.
            Assert.Contains("belum terintegrasi", html);
            Assert.Contains("&mdash;", html);

            // Mockup numbers must NOT appear - they are dummy data, never production values.
            Assert.DoesNotContain("70.334", html);
            Assert.DoesNotContain("154.209", html);
        }
    }

    [Fact]
    public async Task InvalidRange_ReturnsBadRequest()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            using var response = await client.GetAsync("/DailyPerformance?startDate=2026-09-05&endDate=2026-08-23");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task JsonEndpoint_ReturnsPayload_WithSamePeriod()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            using var response = await client.GetAsync($"/DailyPerformance/Report?startDate={FixedStart}&endDate={FixedEnd}");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            // Default System.Text.Json: camelCase names, date-only DateTime serialized
            // with the midnight time component (2026-08-23T00:00:00).
            Assert.Contains("\"currentStart\":\"2026-08-23T00:00:00\"", json);
            Assert.Contains("\"currentEnd\":\"2026-09-05T00:00:00\"", json);
            Assert.Contains("\"previousStart\":\"2026-08-09T00:00:00\"", json);
            Assert.Contains("\"previousEnd\":\"2026-08-22T00:00:00\"", json);
            Assert.Contains("\"hasAnyMetricData\":false", json);
        }
    }

    [Fact]
    public async Task Sidebar_HighlightsDaily_OnlyOnDailyPerformanceRoute()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var dailyHtml = await GetPageHtmlAsync(client);
            var idx = dailyHtml.IndexOf("/DailyPerformance\"", StringComparison.Ordinal);
            Assert.True(idx > 0, "Sidebar link to /DailyPerformance not found");
            var anchorArea = dailyHtml[Math.Max(0, idx - 250)..idx];
            Assert.Contains("active", anchorArea);
        }
    }

    [Fact]
    public async Task DonutCharts_RenderEmptyStateGraphic_WhenDataIsUnavailable()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // Both donuts must render a visible empty-state graphic and must NOT render any
            // interactive arc when the source is unavailable.
            var firstDonutLabel = html.IndexOf("KOMPOSISI INTERACTIONS", StringComparison.Ordinal);
            Assert.True(firstDonutLabel >= 0, "interaction donut section not found");
            var secondDonutLabel = html.IndexOf("KOMPOSISI REACH", StringComparison.Ordinal);
            Assert.True(secondDonutLabel > firstDonutLabel, "viewer donut section not found");

            // The first donut SVG must be between its header and the second donut header.
            var firstDonutSvgStart = html.IndexOf("<svg class=\"cpd-donut", firstDonutLabel, StringComparison.Ordinal);
            Assert.True(firstDonutSvgStart >= 0, "interaction donut SVG not found");
            var secondDonutSvgStart = html.IndexOf("<svg class=\"cpd-donut", secondDonutLabel, StringComparison.Ordinal);
            Assert.True(secondDonutSvgStart >= 0, "viewer donut SVG not found");

            // Both SVGs must contain the empty-state class, the visible track, and the empty
            // label, and neither may contain an interactive arc or a hidden legend.
            var firstDonutTail = html[firstDonutSvgStart..(secondDonutLabel > firstDonutSvgStart + 2000 ? firstDonutSvgStart + 2000 : firstDonutSvgStart + 2000)];
            Assert.Contains("cpd-donut-empty", firstDonutTail);
            Assert.Contains("<circle class=\"cpd-donut-track\"", firstDonutTail);
            Assert.Contains("Data belum tersedia", firstDonutTail);
            Assert.True(firstDonutTail.IndexOf("cpd-donut-arc", StringComparison.Ordinal) < 0,
                "interaction donut renders an interactive arc when data is unavailable");
            // The legend must be ACTUALLY hidden (attribute), not a bogus literal
            // "cpd-legend[hidden]" class name which browsers ignore.
            Assert.True(firstDonutTail.Contains("<ul class=\"cpd-legend\" hidden", StringComparison.Ordinal),
                "interaction donut legend is not hidden when data is unavailable");

            var secondDonutTail = html[secondDonutSvgStart..(html.Length > secondDonutSvgStart + 2000 ? secondDonutSvgStart + 2000 : html.Length)];
            Assert.Contains("cpd-donut-empty", secondDonutTail);
            Assert.Contains("<circle class=\"cpd-donut-track\"", secondDonutTail);
            Assert.Contains("Data belum tersedia", secondDonutTail);
            Assert.True(secondDonutTail.IndexOf("cpd-donut-arc", StringComparison.Ordinal) < 0,
                "viewer donut renders an interactive arc when data is unavailable");
            Assert.True(secondDonutTail.Contains("<ul class=\"cpd-legend\" hidden", StringComparison.Ordinal),
                "viewer donut legend is not hidden when data is unavailable");
        }
    }

    [Fact]
    public async Task Sidebar_DailyItem_RendersRisingChartIcon_AndActiveState()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // The Daily Report sidebar item must now carry the rising-chart SVG icon.
            Assert.Contains("<!-- rising-chart icon -->", html);
            Assert.Contains("<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\"", html);

            // Active highlight must remain on the Daily route.
            var idx = html.IndexOf("/DailyPerformance\"", StringComparison.Ordinal);
            Assert.True(idx > 0, "Sidebar link to /DailyPerformance not found");
            var anchorArea = html[Math.Max(0, idx - 250)..idx];
            Assert.Contains("active", anchorArea);
            Assert.Contains("menu-chart", anchorArea);
        }
    }
}
