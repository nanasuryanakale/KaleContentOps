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
/// Page-level render test for the "Metrik Shop Video" section through the REAL MVC
/// pipeline (AuthTestFactory + cookie auth). The registered IAccountDailyMetricSource
/// is swapped for a scripted fake that supplies the verified shop-video funnel fields
/// (product_impressions, product_clicks) plus video_views, so the assertions run
/// against the actual Razor output the operator sees - not against a fabricated model.
/// Numeric correctness of the aggregation is covered by DailyPerformanceServiceTests.
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
    public async Task Page_RendersShopVideoSection_WithRealImpressionsAndClicks()
    {
        var html = await GetPageHtmlAsync();

        // The new section exists with its own heading.
        Assert.Contains("METRIK SHOP VIDEO", html);
        var shop = Section(html, "aria-label=\"Metrik Shop Video\"");

        // Product Impressions: 14 days x 1.000 = 14.000 (id-ID grouping), own card.
        Assert.Contains("data-metric=\"product_impressions\"", shop);
        Assert.Contains("14.000", shop);

        // Product Clicks: 14 days x 50 = 700, own card.
        Assert.Contains("data-metric=\"product_clicks\"", shop);
        Assert.Contains(">700<", shop);

        // Period delta of the current vs previous window (14.000 vs 11.200 = +25.0%).
        // Razor HTML-encodes "+" as &#x2B; in the rendered badge.
        Assert.Contains("cpd-card-delta up", shop);
        Assert.Contains("&#x2B;25.0% vs periode sebelumnya", shop);

        // The section explicitly states these are shop-funnel metrics, not engagement.
        Assert.Contains("product_impressions", shop);
    }

    [Fact]
    public async Task Page_KeepsShopFunnelOutOfSummaryCards_AndComparisonTable()
    {
        var html = await GetPageHtmlAsync();

        // Summary cards still carry Video Views with its real value (14 x 2.000 = 28.000)
        // and never the shop-funnel metrics.
        var cards = Section(html, "aria-label=\"Ringkasan metrik\"");
        Assert.Contains("VIDEO VIEWS", cards);
        Assert.Contains("28.000", cards);
        Assert.DoesNotContain("data-metric=\"product_impressions\"", cards);
        Assert.DoesNotContain("data-metric=\"product_clicks\"", cards);

        // The period comparison table stays account-engagement only.
        var comparison = Section(html, "PERBANDINGAN PERIODE");
        Assert.DoesNotContain("data-metric=\"product_impressions\"", comparison);
        Assert.DoesNotContain("data-metric=\"product_clicks\"", comparison);

        // The footer keeps documenting both metrics with their verified source.
        Assert.Contains("Jumlah tampilan produk (product_impressions)", html);
        Assert.Contains("Jumlah klik produk (product_clicks)", html);
    }
}
