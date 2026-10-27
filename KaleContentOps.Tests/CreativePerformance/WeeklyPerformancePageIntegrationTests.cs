using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Services;
using KaleContentOps.Services.CreativePerformance;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// End-to-end page tests through the real MVC pipeline (AuthTestFactory): the
/// /WeeklyPerformance route resolves, the page renders every mockup section with the
/// BLOCKED (unavailable) provider state - no fabricated zeros - the chart payload
/// travels as embedded JSON, both donuts show an honest empty state, and the sidebar
/// highlights Weekly only on this route. Numeric correctness (bucketing, period
/// comparison, ER) is covered by WeeklyPerformanceServiceTests.
/// </summary>
public class WeeklyPerformancePageIntegrationTests
{
    // The documented example range: Monday-Sunday weeks in Asia/Jakarta produce
    // buckets of 1, 7 and 6 days (23 Aug is a Sunday, 5 Sep is a Saturday).
    private const string FixedStart = "2026-08-23";
    private const string FixedEnd = "2026-09-05";

    private static async Task<(AuthTestFactory Factory, HttpClient Client)> CreateSignedInAsync()
    {
        var factory = new AuthTestFactory();
        var username = "cpw.page";
        await factory.CreateRoleUserAsync(username, Security.AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);
        return (factory, client);
    }

    private static async Task<string> GetPageHtmlAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"/WeeklyPerformance?startDate={FixedStart}&endDate={FixedEnd}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Anonymous_Request_Redirects_To_Login()
    {
        using var factory = new AuthTestFactory();
        var client = factory.CreateBrowserClient(allowAutoRedirect: false);

        using var response = await client.GetAsync("/WeeklyPerformance");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Page_Renders_AllMockupSections_AndSelectedRange()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // Header + date picker contract (selected range echoed back).
            Assert.Contains("Creative Performance", html);
            Assert.Contains("Weekly Report", html);
            Assert.Contains("dateRangeButton", html);
            Assert.Contains("23/08", html);
            Assert.Contains("05/09", html);

            // Included-day counts for the selected period and its comparison period.
            Assert.Contains("(14 hari)", html);
            Assert.Contains("GMT+7", html);

            // Seven summary cards in mockup order.
            foreach (var label in new[] { "REACH", "VIDEO VIEWS", "PROFILE VIEWS", "INTERACTIONS", "ENGAGEMENT RATE", "NET FOLLOWER", "TOTAL FOLLOWER" })
            {
                Assert.Contains(label, html);
            }

            // Period comparison + weekly detail table + both charts + both donuts.
            Assert.Contains("PERBANDINGAN PERIODE", html);
            Assert.Contains("DETAIL MINGGU", html);

            // Weekly buckets render with day counts and partial-week indicators
            // (23 Aug alone = 1 day, 31 Aug-5 Sep = 6 days, both partial weeks).
            Assert.Contains("Pekan parsial", html);
            Assert.Contains("6 hari", html);
            Assert.Contains("1 hari", html);

            Assert.Contains("TREN REACH", html);
            Assert.Contains("PERGERAKAN NET FOLLOWER", html);
            Assert.Contains("KOMPOSISI INTERACTIONS", html);
            Assert.Contains("VIEWER BARU VS LAMA", html);

            // Chart payload travels as embedded JSON for the client renderer.
            Assert.Contains("id=\"cpdChartPayload\"", html);
            Assert.Contains("\"trend\":", html);
        }
    }

    [Fact]
    public async Task Page_ShowsBlockedSourceNotice_AndDashes_NotFakeZeros()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // Honest empty state: the em dash literal, the chart's empty message,
            // and the provider-blocked notice - no metric ever shows a fake zero.
            Assert.Contains("&mdash;", html);
            Assert.Contains("belum terintegrasi", html);
            Assert.Contains("&#x2014; vs periode sebelumnya", html);

            // Mockup numbers must NEVER appear - they are dummy data, never production values.
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
            using var response = await client.GetAsync("/WeeklyPerformance?startDate=2026-09-05&endDate=2026-08-23");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task JsonEndpoint_ReturnsPayload_WithEqualLengthPreviousPeriod_AndBucketCounts()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            using var response = await client.GetAsync($"/WeeklyPerformance/Report?startDate={FixedStart}&endDate={FixedEnd}");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();

            // Current 23/08-05/09 (14 days) compares against the 14 days ending
            // the day before: 09/08-22/08. Same contract the Daily Report uses.
            Assert.Contains("\"currentStart\":\"2026-08-23T00:00:00\"", json);
            Assert.Contains("\"currentEnd\":\"2026-09-05T00:00:00\"", json);
            Assert.Contains("\"previousStart\":\"2026-08-09T00:00:00\"", json);
            Assert.Contains("\"previousEnd\":\"2026-08-22T00:00:00\"", json);

            // Monday-Sunday bucketing of the example range: 1 + 7 + 6 days = 3 buckets.
            Assert.Contains("\"currentBucketCount\":3", json);
            Assert.Contains("\"previousBucketCount\":3", json);

            // No data source in the test environment - honestly reported, never faked.
            Assert.Contains("\"hasAnyMetricData\":false", json);
        }
    }

    [Fact]
    public async Task DonutCharts_RenderEmptyStateGraphic_WhenDataIsUnavailable()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            var firstDonutLabel = html.IndexOf("KOMPOSISI INTERACTIONS", StringComparison.Ordinal);
            Assert.True(firstDonutLabel >= 0, "interaction donut section not found");
            var secondDonutLabel = html.IndexOf("KOMPOSISI REACH", StringComparison.Ordinal);
            Assert.True(secondDonutLabel > firstDonutLabel, "viewer donut section not found");

            var firstDonutSvgStart = html.IndexOf("<svg class=\"cpd-donut", firstDonutLabel, StringComparison.Ordinal);
            Assert.True(firstDonutSvgStart >= 0, "interaction donut SVG not found");
            var secondDonutSvgStart = html.IndexOf("<svg class=\"cpd-donut", secondDonutLabel, StringComparison.Ordinal);
            Assert.True(secondDonutSvgStart >= 0, "viewer donut SVG not found");

            // Both SVGs: empty-state class, visible track, honest empty label, NO arc.
            var firstDonutTail = html[firstDonutSvgStart..Math.Min(firstDonutSvgStart + 2000, html.Length)];
            Assert.Contains("cpd-donut-empty", firstDonutTail);
            Assert.Contains("<circle class=\"cpd-donut-track\"", firstDonutTail);
            Assert.Contains("Data belum tersedia", firstDonutTail);
            Assert.True(firstDonutTail.IndexOf("cpd-donut-arc", StringComparison.Ordinal) < 0,
                "interaction donut renders an interactive arc when data is unavailable");
            Assert.True(firstDonutTail.Contains("<ul class=\"cpd-legend\" hidden", StringComparison.Ordinal),
                "interaction donut legend is not hidden when data is unavailable");

            var secondDonutTail = html[secondDonutSvgStart..Math.Min(secondDonutSvgStart + 2000, html.Length)];
            Assert.Contains("cpd-donut-empty", secondDonutTail);
            Assert.Contains("Data belum tersedia", secondDonutTail);
            Assert.True(secondDonutTail.IndexOf("cpd-donut-arc", StringComparison.Ordinal) < 0,
                "viewer donut renders an interactive arc when data is unavailable");
            Assert.True(secondDonutTail.Contains("<ul class=\"cpd-legend\" hidden", StringComparison.Ordinal),
                "viewer donut legend is not hidden when data is unavailable");

            // No leftover placeholder glyph from the pre-arc markup (was <text>>Data ...).
            Assert.DoesNotContain(">>Data", html);
        }
    }

    /// <summary>
    /// Fully-populated fake: two real-shaped weekly buckets plus an AVAILABLE
    /// interaction composition, so the server-rendered donut arcs and the chart
    /// payload values execute for real (the pipeline's default provider is blocked).
    /// </summary>
    private sealed class PopulatedService : IWeeklyPerformanceService
    {
        public Task<WeeklyReportData> BuildAsync(WeeklyReportFilter filter, CancellationToken cancellationToken = default)
        {
            var row1 = new WeeklyReportRow
            {
                Start = new DateTime(2026, 8, 24),
                End = new DateTime(2026, 8, 30),
                DayCount = 7,
                Reach = 5000,
                VideoViews = 12000
            };
            var row2 = new WeeklyReportRow
            {
                Start = new DateTime(2026, 8, 31),
                End = new DateTime(2026, 9, 5),
                DayCount = 6,
                IsPartial = true,
                Reach = 7000,
                VideoViews = 15000
            };

            return Task.FromResult(new WeeklyReportData
            {
                CurrentStart = filter.StartDate,
                CurrentEnd = filter.EndDate,
                PreviousStart = new DateTime(2026, 8, 9),
                PreviousEnd = new DateTime(2026, 8, 22),
                CurrentDayCount = 14,
                PreviousDayCount = 14,
                CurrentBucketCount = 2,
                PreviousBucketCount = 2,
                HasAnyMetricData = true,
                Trend = new[] { row1, row2 },
                WeeklyRows = new[] { row2, row1 },
                InteractionComposition = new DailyReportComposition
                {
                    Key = "interaction_composition",
                    Title = "Komposisi interactions",
                    IsAvailable = true,
                    Total = 400m,
                    Segments = new[]
                    {
                        new DailyReportCompositionSegment { Key = "likes", Label = "Likes", Color = "#4d9fff", Value = 240m, Percentage = 60m },
                        new DailyReportCompositionSegment { Key = "comments", Label = "Comments", Color = "#7c8cff", Value = 100m, Percentage = 25m },
                        new DailyReportCompositionSegment { Key = "shares", Label = "Shares", Color = "#39d0a4", Value = 60m, Percentage = 15m }
                    }
                },
                ViewerComposition = new DailyReportComposition
                {
                    Key = "viewer_composition",
                    IsAvailable = false,
                    UnavailableReason = "Sumber composition viewer belum tersedia"
                },
                Cards = Array.Empty<DailyReportMetric>(),
                ComparisonRows = Array.Empty<DailyReportMetric>(),
                Notices = Array.Empty<string>(),
                SourceNotes = Array.Empty<DailyReportSourceNote>(),
                TimeZoneId = ShopTimeZoneOptions.DefaultTimeZoneId,
                TimeZoneLabel = "GMT+7"
            });
        }
    }

    private sealed class PopulatedFactory : AuthTestFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IWeeklyPerformanceService>();
            services.AddScoped<IWeeklyPerformanceService, PopulatedService>();
        }
    }

    /// <summary>Extracts the substring of the first section opened after <paramref name="marker"/>.</summary>
    private static string Section(string html, string marker)
    {
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"marker not found in page: {marker}");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"section end not found after marker: {marker}");
        return html[start..end];
    }

    [Fact]
    public async Task Page_RendersDonutArcs_AndChartPayload_WhenCompositionAvailable()
    {
        using var factory = new PopulatedFactory();
        const string username = "cpw.populated";
        await factory.CreateRoleUserAsync(username, Security.AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);

        using var response = await client.GetAsync($"/WeeklyPerformance?startDate={FixedStart}&endDate={FixedEnd}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Chart payload carries the backend trend values (never zeroed, never invented).
        Assert.Contains("\"reach\":5000", html);
        Assert.Contains("\"videoViews\":15000", html);

        // Interaction donut: server-rendered arcs from real percentages + total + visible legend.
        var interactionSection = Section(html, "KOMPOSISI INTERACTIONS");
        Assert.Contains("cpd-donut-arc", interactionSection);
        Assert.Contains("stroke-dasharray", interactionSection);
        Assert.Contains("cpd-donut-total", interactionSection);
        Assert.Contains(">400<", interactionSection);
        Assert.Contains("60.00%", interactionSection);
        Assert.DoesNotContain("cpd-donut-empty", interactionSection);
        Assert.DoesNotContain("<ul class=\"cpd-legend\" hidden", interactionSection);

        // Viewer composition stays unavailable (approved rule) with its honest empty state.
        var viewerSection = Section(html, "VIEWER BARU VS LAMA");
        Assert.Contains("cpd-donut-empty", viewerSection);
        Assert.Contains("Data belum tersedia", viewerSection);
        Assert.DoesNotContain("cpd-donut-arc", viewerSection);
        Assert.Contains("<ul class=\"cpd-legend\" hidden", viewerSection);
    }

    [Fact]
    public async Task Page_SectionOrder_MatchesMockup_DetailMingguComesLast()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            int At(string marker)
            {
                var idx = html.IndexOf(marker, StringComparison.Ordinal);
                Assert.True(idx >= 0, $"section marker not found: {marker}");
                return idx;
            }

            var comparison = At("PERBANDINGAN PERIODE");
            var trend = At("TREN REACH");
            var net = At("PERGERAKAN NET FOLLOWER");
            var donuts = At("KOMPOSISI INTERACTIONS");
            var detail = At("DETAIL MINGGU");

            // Mockup document order: comparison -> trend -> net -> donuts -> detail table last.
            Assert.True(comparison < trend, "comparison must precede the trend chart");
            Assert.True(trend < net, "trend chart must precede the net follower card");
            Assert.True(net < donuts, "net follower card must precede the composition donuts");
            Assert.True(donuts < detail, "DETAIL MINGGU must be the LAST main analytics section");

            // The donut section sits above the table exactly once (no duplicate markup).
            var secondDetail = html.IndexOf("DETAIL MINGGU", detail + 1, StringComparison.Ordinal);
            Assert.True(secondDetail < 0, "DETAIL MINGGU section appears more than once");
        }
    }

    [Fact]
    public async Task Page_RendersSevenKpiCards_AndMockupTableColumns()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // All seven KPI cards keep their container even while data is unavailable.
            Assert.Equal(7, CountOccurrences(html, "cpd-card-label"));
            foreach (var metric in new[] { "reach", "video_views", "profile_views", "interactions", "engagement_rate", "net_follower", "total_follower" })
            {
                Assert.Contains($"data-metric=\"{metric}\"", html);
            }

            // Unavailable cards stay unstructured-free: values render as the em dash, never 0.
            Assert.Contains("cpd-card-unavailable", html);

            // Mockup detail-table columns (sentence case, includes day count + Profile Views).
            Assert.Contains("<th>Minggu</th>", html);
            Assert.Contains("<th class=\"cpd-num\">Hari</th>", html);
            Assert.Contains("<th class=\"cpd-num\">Profile Views</th>", html);
            Assert.Contains("<th class=\"cpd-num\">Engagement Rate</th>", html);
            Assert.DoesNotContain("<th class=\"cpd-num\">TANGGAL</th>", html);

            // Framed chart empty states (intentional, not leftover text).
            Assert.True(CountOccurrences(html, "cpd-chart-empty") >= 2,
                "trend and net follower cards must each show a framed empty state");

            // Date range uses the shared component markup of the other menus.
            Assert.Contains("popover-panel date-picker-panel", html);
            Assert.Contains("calendar-icon", html);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public async Task Sidebar_HighlightsWeekly_OnlyOnWeeklyRoute_MonthlyStaysPlaceholder()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            var idx = html.IndexOf("/WeeklyPerformance\"", StringComparison.Ordinal);
            Assert.True(idx > 0, "Sidebar link to /WeeklyPerformance not found");
            var anchorArea = html[Math.Max(0, idx - 250)..idx];
            Assert.Contains("active", anchorArea);
            Assert.Contains("menu-chart", anchorArea);

            // Monthly remains a non-route placeholder (module not built yet).
            Assert.Contains("href=\"#\">Monthly</a>", html);
        }
    }
}
