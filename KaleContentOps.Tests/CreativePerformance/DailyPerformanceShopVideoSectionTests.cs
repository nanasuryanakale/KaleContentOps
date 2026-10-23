using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Services.CreativePerformance;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// Page-level REMOVAL regression test (mockup alignment, 2026-10-23) through the REAL
/// MVC pipeline (AuthTestFactory + cookie auth). The "Metrik Shop Video", "Detail Shop
/// Video Harian" and "Definisi metrik &amp; sumber data" sections were removed from the
/// Daily Report view; these tests prove they are really gone (no heading, no card, no
/// empty container) while the sections that STAY (summary cards, PERBANDINGAN PERIODE,
/// DETAIL HARIAN) keep rendering their values from the same registered source.
/// The registered IAccountDailyMetricSource is a scripted fake supplying the verified
/// shop-video funnel fields plus video_views, so assertions run against the actual
/// Razor output the operator sees.
/// </summary>
public class DailyPerformanceShopVideoSectionTests
{
    private const string Start = "2026-08-23";
    private const string End = "2026-09-05";

    /// <summary>
    /// Verified-partial source: declares Video Views + the shop-video commerce funnel,
    /// with deterministic per-day values for the current and the previous period.
    /// </summary>
    private sealed class ShopSource : IAccountDailyMetricSource
    {
        public Task<AccountDailyMetricResult> GetAsync(AccountDailyMetricQuery query, CancellationToken cancellationToken = default)
        {
            // Current period (23 Aug - 5 Sep) is bigger than the previous one on purpose,
            // so the delta badge has a real growth to render.
            var isPrevious = query.Start < new DateTime(2026, 8, 23);
            var records = new List<AccountDailyMetricRecord>();
            for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
            {
                records.Add(new AccountDailyMetricRecord
                {
                    Date = day,
                    Reach = 100,
                    VideoViews = 2000,
                    ProfileViews = 20,
                    Likes = 8,
                    Comments = 1,
                    Shares = 1,
                    FollowersGained = 5,
                    FollowersLost = 1,
                    ProductImpressions = isPrevious ? 800 : 1000,
                    ProductClicks = isPrevious ? 40 : 50
                });
            }

            return Task.FromResult(AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "scripted-shop-overview-source",
                AvailableMetricKeys = new HashSet<string>
                {
                    DailyReportMetrics.Reach,
                    DailyReportMetrics.VideoViews,
                    DailyReportMetrics.ProfileViews,
                    DailyReportMetrics.Interactions,
                    DailyReportMetrics.EngagementRate,
                    DailyReportMetrics.NetFollower,
                    DailyReportMetrics.TotalFollower,
                    DailyReportMetrics.ProductImpressions,
                    DailyReportMetrics.ProductClicks,
                    DailyReportMetrics.VideoAttributedSkuOrders
                }
            }));
        }
    }

    private sealed class ShopSectionFactory : AuthTestFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IAccountDailyMetricSource>();
            services.AddScoped<IAccountDailyMetricSource, ShopSource>();
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

    private static async Task<string> GetPageHtmlAsync()
    {
        var factory = new ShopSectionFactory();
        using (factory)
        {
            const string username = "cpd.shop.render";
            await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
            var client = await factory.SignInAsync(username);

            using var response = await client.GetAsync($"/DailyPerformance?startDate={Start}&endDate={End}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
    }

    [Fact]
    public async Task Page_HasNoShopVideoSections_AndNoEmptyLeftovers()
    {
        var html = await GetPageHtmlAsync();

        // Headings and containers of both removed sections are gone...
        Assert.DoesNotContain("METRIK SHOP VIDEO", html);
        Assert.DoesNotContain("DETAIL SHOP VIDEO HARIAN", html);
        Assert.DoesNotContain("cpd-shop-video", html);
        Assert.DoesNotContain("cpd-shop-daily", html);

        // ...including every card and table cell that belonged to them.
        Assert.DoesNotContain("data-metric=\"product_impressions\"", html);
        Assert.DoesNotContain("data-metric=\"product_clicks\"", html);
        Assert.DoesNotContain("VIDEO-ATTR GMV", html);
        Assert.DoesNotContain("IMPRESSIONS", html);

        // The metric-definitions footer is removed as well - no title, no notes list.
        Assert.DoesNotContain("Definisi metrik", html);
        Assert.DoesNotContain("cpd-footer", html);

        // And no other page section was lost with them.
        Assert.Contains("PERBANDINGAN PERIODE", html);
        Assert.Contains("DETAIL HARIAN", html);
    }

    [Fact]
    public async Task Page_KeepsSummaryCardsAndComparison_WithRealValues()
    {
        var html = await GetPageHtmlAsync();

        // Summary cards still render with real values (14 days x 2.000 = 28.000).
        var cards = Section(html, "aria-label=\"Ringkasan metrik\"");
        Assert.Contains("VIDEO VIEWS", cards);
        Assert.Contains("28.000", cards);
        Assert.Contains("INTERACTIONS", cards);
        Assert.DoesNotContain("data-metric=\"product_impressions\"", cards);
        Assert.DoesNotContain("data-metric=\"product_clicks\"", cards);

        // The period comparison table stays account-engagement only and keeps its delta.
        // (Comparison rows render the metric label in title case.)
        var comparison = Section(html, "PERBANDINGAN PERIODE");
        Assert.Contains("Interactions", comparison);
        Assert.DoesNotContain("data-metric=\"product_impressions\"", comparison);
        Assert.DoesNotContain("data-metric=\"product_clicks\"", comparison);

        // DETAIL HARIAN keeps its per-day account columns (incl. daily interactions).
        // (Table headers are uppercase.)
        var daily = Section(html, "DETAIL HARIAN");
        Assert.Contains("INTERACTIONS", daily);
        Assert.Contains("ENGAGEMENT RATE", daily);
    }
}
