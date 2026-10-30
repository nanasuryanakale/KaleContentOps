using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
/// /MonthlyPerformance route resolves, the page renders every mockup section with
/// the BLOCKED (unavailable) provider state - no fabricated zeros - the chart
/// payload travels as embedded JSON, both donuts show an honest empty state, the
/// date picker echoes the effective range, and the sidebar highlights Monthly on
/// this route. Numeric correctness (bucketing, period comparison, ER) is covered
/// by MonthlyPerformanceServiceTests.
/// </summary>
public class MonthlyPerformancePageIntegrationTests
{
    // A range crossing two calendar months (partial August + partial September).
    private const string FixedStart = "2026-08-23";
    private const string FixedEnd = "2026-09-05";

    private static async Task<(AuthTestFactory Factory, HttpClient Client)> CreateSignedInAsync()
    {
        var factory = new AuthTestFactory();
        var username = "cpm.page";
        await factory.CreateRoleUserAsync(username, Security.AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);
        return (factory, client);
    }

    private static async Task<string> GetPageHtmlAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"/MonthlyPerformance?startDate={FixedStart}&endDate={FixedEnd}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
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
    public async Task Anonymous_Request_Redirects_To_Login()
    {
        using var factory = new AuthTestFactory();
        var client = factory.CreateBrowserClient(allowAutoRedirect: false);

        using var response = await client.GetAsync("/MonthlyPerformance");

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
            Assert.Contains("Monthly Report", html);
            Assert.Contains("dateRangeButton", html);
            Assert.Contains("23/08", html);
            Assert.Contains("05/09", html);

            // Day counts and bucket counts for the period and its comparison period.
            Assert.Contains("(14 hari, 2 bulan)", html);
            Assert.Contains("(14 hari, 1 bulan)", html);
            Assert.Contains("GMT+7", html);

            // Seven summary cards in mockup order.
            foreach (var label in new[] { "REACH", "VIDEO VIEWS", "PROFILE VIEWS", "INTERACTIONS", "ENGAGEMENT RATE", "NET FOLLOWER", "TOTAL FOLLOWER" })
            {
                Assert.Contains(label, html);
            }

            // Period comparison + charts + donuts + monthly detail table + footer.
            Assert.Contains("PERBANDINGAN PERIODE", html);
            Assert.Contains("TREN REACH", html);
            Assert.Contains("PERGERAKAN NET FOLLOWER", html);
            Assert.Contains("KOMPOSISI INTERACTIONS", html);
            Assert.Contains("KOMPOSISI REACH", html);
            Assert.Contains("DETAIL BULAN", html);
            Assert.Contains("SUMBER TARIKAN PER METRIK", html);

            // Monthly buckets render with day counts + partial-month indicators.
            Assert.Contains("Bulan parsial", html);
            Assert.Contains("9 hari", html);
            Assert.Contains("5 hari", html);

            // Chart payload carries the backend month rows (oldest first).
            Assert.Contains("\"date\":\"2026-08-23\"", html);
            Assert.Contains("\"date\":\"2026-09-01\"", html);

            // The sidebar highlights Monthly on this route only.
            Assert.Contains("menu-chart active\" href=\"/MonthlyPerformance\"", html);
        }
    }

    [Fact]
    public async Task Page_ShowsHonestUnavailableStates_WhenProviderIsBlocked()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            // Unavailable cards keep their container and render "—", never 0.
            Assert.Contains("cpd-card-unavailable", html);
            Assert.Equal(7, CountOccurrences(html, "cpd-card-label"));

            // Both donuts: empty-state graphic, hidden legend, honest reason.
            // (Count the exact class token: "cpd-donut-empty-mark"/-text also
            // start with the same prefix but are inner markup classes.)
            Assert.Equal(2, CountOccurrences(html, "cpd-donut-empty\""));
            Assert.Equal(2, CountOccurrences(html, "Data belum tersedia"));
            Assert.Equal(2, CountOccurrences(html, "<ul class=\"cpd-legend\" hidden"));
            Assert.True(html.IndexOf("cpd-donut-arc", StringComparison.Ordinal) < 0,
                "donut arcs must not render when composition data is unavailable");

            // The charts never draw fabricated zero points without a source.
            Assert.Contains("tanpa titik nol fiktif", html);
            Assert.Contains("tanpa batang nol fiktif", html);
        }
    }

    [Fact]
    public async Task Report_ReturnsJson_WithMonthBuckets_AndHonestEmptyData()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            using var response = await client.GetAsync($"/MonthlyPerformance/Report?startDate={FixedStart}&endDate={FixedEnd}");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            Assert.Contains("\"currentStart\":\"2026-08-23T00:00:00\"", json);
            Assert.Contains("\"currentEnd\":\"2026-09-05T00:00:00\"", json);
            Assert.Contains("\"previousStart\":\"2026-08-09T00:00:00\"", json);
            Assert.Contains("\"previousEnd\":\"2026-08-22T00:00:00\"", json);

            // Calendar-month bucketing of the example range: Aug + Sep = 2 buckets;
            // the equal-length previous period (9-22 Aug) is 1 bucket.
            Assert.Contains("\"currentBucketCount\":2", json);
            Assert.Contains("\"previousBucketCount\":1", json);

            // No verified account-level data in the test environment - honestly
            // reported, never faked.
            Assert.Contains("\"hasAnyMetricData\":false", json);
        }
    }

    [Fact]
    public async Task Page_SectionOrder_MatchesMockup_DetailBulanComesLast()
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
            var detail = At("DETAIL BULAN");

            // Mockup document order: comparison -> trend -> net -> donuts -> detail table last.
            Assert.True(comparison < trend, "comparison must precede the trend chart");
            Assert.True(trend < net, "trend chart must precede the net follower card");
            Assert.True(net < donuts, "net follower card must precede the composition donuts");
            Assert.True(donuts < detail, "DETAIL BULAN must be the LAST main analytics section");

            // The detail section sits above the footer exactly once.
            var secondDetail = html.IndexOf("DETAIL BULAN", detail + 1, StringComparison.Ordinal);
            Assert.True(secondDetail < 0, "DETAIL BULAN section appears more than once");
        }
    }

    [Fact]
    public async Task Page_RendersSevenKpiCards_AndMockupTableColumns()
    {
        var (factory, client) = await CreateSignedInAsync();
        using (factory)
        {
            var html = await GetPageHtmlAsync(client);

            foreach (var metric in new[] { "reach", "video_views", "profile_views", "interactions", "engagement_rate", "net_follower", "total_follower" })
            {
                Assert.Contains($"data-metric=\"{metric}\"", html);
            }

            // Mockup monthly-detail-table columns.
            Assert.Contains("<th>Bulan</th>", html);
            Assert.Contains("<th class=\"cpd-num\">Hari</th>", html);
            Assert.Contains("<th class=\"cpd-num\">Profile Views</th>", html);
            Assert.Contains("<th class=\"cpd-num\">Engagement Rate</th>", html);
        }
    }

    /// <summary>
    /// Fully-populated fake service so the server-rendered donut arcs and the
    /// chart payload values execute for real (the pipeline's default provider is
    /// blocked in the test environment).
    /// </summary>
    private sealed class PopulatedService : IMonthlyPerformanceService
    {
        public Task<MonthlyReportData> BuildAsync(MonthlyReportFilter filter, CancellationToken cancellationToken = default)
        {
            var august = new MonthlyReportRow
            {
                Start = new DateTime(2026, 8, 23),
                End = new DateTime(2026, 8, 31),
                DayCount = 9,
                IsPartial = true,
                Reach = 5000,
                VideoViews = 12000
            };
            var september = new MonthlyReportRow
            {
                Start = new DateTime(2026, 9, 1),
                End = new DateTime(2026, 9, 5),
                DayCount = 5,
                IsPartial = true,
                Reach = 7000,
                VideoViews = 15000,
                NetFollower = -12
            };

            return Task.FromResult(new MonthlyReportData
            {
                CurrentStart = filter.StartDate,
                CurrentEnd = filter.EndDate,
                PreviousStart = new DateTime(2026, 8, 9),
                PreviousEnd = new DateTime(2026, 8, 22),
                CurrentDayCount = 14,
                PreviousDayCount = 14,
                CurrentBucketCount = 2,
                PreviousBucketCount = 1,
                HasAnyMetricData = true,
                Trend = new[] { august, september },
                MonthlyRows = new[] { september, august },
                InteractionComposition = new DailyReportComposition
                {
                    Key = "interaction_composition",
                    Title = "Komposisi Interactions",
                    IsAvailable = true,
                    Total = 400m,
                    Segments = new[]
                    {
                        new DailyReportCompositionSegment { Key = "like", Label = "Like", Color = "#4d9fff", Value = 240m, Percentage = 60m },
                        new DailyReportCompositionSegment { Key = "comment", Label = "Comment", Color = "#e8b83f", Value = 100m, Percentage = 25m },
                        new DailyReportCompositionSegment { Key = "share", Label = "Share", Color = "#a975e8", Value = 60m, Percentage = 15m }
                    }
                },
                ViewerComposition = new DailyReportComposition
                {
                    Key = "viewer_composition",
                    IsAvailable = false,
                    UnavailableReason = "Komposisi viewer level akun belum tersedia"
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
            services.RemoveAll<IMonthlyPerformanceService>();
            services.AddScoped<IMonthlyPerformanceService, PopulatedService>();
        }
    }

    [Fact]
    public async Task Page_RendersDonutArcs_AndChartPayload_WhenCompositionAvailable()
    {
        using var factory = new PopulatedFactory();
        const string username = "cpm.populated";
        await factory.CreateRoleUserAsync(username, Security.AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);

        using var response = await client.GetAsync($"/MonthlyPerformance?startDate={FixedStart}&endDate={FixedEnd}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Chart payload carries the backend month values (never zeroed, never invented).
        Assert.Contains("\"reach\":5000", html);
        Assert.Contains("\"videoViews\":15000", html);
        Assert.Contains("\"netFollower\":-12", html);

        // Interaction donut: server-rendered arcs + total + visible legend.
        var start = html.IndexOf("KOMPOSISI INTERACTIONS", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var section = html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
        Assert.Contains("cpd-donut-arc", section);
        Assert.Contains("stroke-dasharray", section);
        Assert.Contains("cpd-donut-total", section);
        Assert.Contains(">400<", section);
        Assert.Contains("60.00%", section);
        Assert.DoesNotContain("cpd-donut-empty", section);
        Assert.DoesNotContain("<ul class=\"cpd-legend\" hidden", section);

        // Viewer composition stays unavailable (approved rule) with honest empty state.
        var viewerStart = html.IndexOf("VIEWER BARU VS LAMA", StringComparison.Ordinal);
        Assert.True(viewerStart >= 0);
        var viewerSection = html[viewerStart..html.IndexOf("</section>", viewerStart, StringComparison.Ordinal)];
        Assert.Contains("cpd-donut-empty", viewerSection);
        Assert.Contains("Data belum tersedia", viewerSection);
        Assert.DoesNotContain("cpd-donut-arc", viewerSection);
        Assert.Contains("<ul class=\"cpd-legend\" hidden", viewerSection);
    }

    // ------------------------------------------------------------------
    // Trend chart payload: one-month periods (single marker) vs multi-month
    // periods (connected points). The renderer must receive exactly the backend
    // rows - no invented points, no zeroed values.
    // ------------------------------------------------------------------

    private static string ChartPayload(string html)
    {
        const string open = "<script type=\"application/json\" id=\"cpdChartPayload\">";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "chart payload script missing from the page");
        start += open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        Assert.True(end > start, "chart payload script not terminated");
        return html[start..end];
    }

    /// <summary>Fake service covering exactly ONE month bucket (1..29 Oktober 2026).</summary>
    private sealed class SingleMonthService : IMonthlyPerformanceService
    {
        public Task<MonthlyReportData> BuildAsync(MonthlyReportFilter filter, CancellationToken cancellationToken = default)
        {
            var october = new MonthlyReportRow
            {
                Start = new DateTime(2026, 10, 1),
                End = new DateTime(2026, 10, 29),
                DayCount = 29,
                IsPartial = true,
                VideoViews = 1192062 // Reach stays null: no verified source
            };

            return Task.FromResult(new MonthlyReportData
            {
                CurrentStart = filter.StartDate,
                CurrentEnd = filter.EndDate,
                PreviousStart = new DateTime(2026, 9, 2),
                PreviousEnd = new DateTime(2026, 9, 30),
                CurrentDayCount = 29,
                PreviousDayCount = 29,
                CurrentBucketCount = 1,
                PreviousBucketCount = 1,
                HasAnyMetricData = true,
                Trend = new[] { october },
                MonthlyRows = new[] { october },
                InteractionComposition = new DailyReportComposition
                {
                    Key = "interaction_composition",
                    Title = "Komposisi Interactions",
                    IsAvailable = false,
                    UnavailableReason = "Komponen interaksi tidak lengkap pada periode ini - komposisi tidak ditampilkan agar tidak menyesatkan."
                },
                ViewerComposition = new DailyReportComposition
                {
                    Key = "viewer_composition",
                    IsAvailable = false,
                    UnavailableReason = "Komposisi viewer level akun belum tersedia"
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

    private sealed class SingleMonthFactory : AuthTestFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IMonthlyPerformanceService>();
            services.AddScoped<IMonthlyPerformanceService, SingleMonthService>();
        }
    }

    [Fact]
    public async Task Page_SingleMonthTrend_CarriesOnePointWithVideoViews_ForTheSingleMarker()
    {
        using var factory = new SingleMonthFactory();
        const string username = "cpm.singlemonth";
        await factory.CreateRoleUserAsync(username, Security.AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);

        using var response = await client.GetAsync("/MonthlyPerformance?startDate=2026-10-01&endDate=2026-10-29");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(ChartPayload(html));
        var trend = doc.RootElement.GetProperty("trend");

        // Exactly ONE bucket: the renderer draws a marker (never a line, never a
        // fabricated second point) at the real backend value.
        Assert.Equal(1, trend.GetArrayLength());
        var point = trend[0];
        Assert.Equal("2026-10-01", point.GetProperty("date").GetString());
        Assert.Equal(1192062, point.GetProperty("videoViews").GetInt64());
        Assert.Equal(JsonValueKind.Null, point.GetProperty("reach").ValueKind);
    }

    [Fact]
    public async Task Page_TwoMonthTrend_CarriesBothPoints_ForTheConnectedLine()
    {
        using var factory = new PopulatedFactory();
        const string username = "cpm.twomonth";
        await factory.CreateRoleUserAsync(username, Security.AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);

        using var response = await client.GetAsync($"/MonthlyPerformance?startDate={FixedStart}&endDate={FixedEnd}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(ChartPayload(html));
        var trend = doc.RootElement.GetProperty("trend");

        Assert.Equal(2, trend.GetArrayLength());
        Assert.Equal(12000, trend[0].GetProperty("videoViews").GetInt64());
        Assert.Equal(15000, trend[1].GetProperty("videoViews").GetInt64());
        Assert.Equal(5000, trend[0].GetProperty("reach").GetInt64());
    }
}
