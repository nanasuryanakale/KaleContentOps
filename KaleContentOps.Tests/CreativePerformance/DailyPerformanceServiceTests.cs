using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Services;
using KaleContentOps.Services.CreativePerformance;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// Service-level behaviour: period derivation, card/comparison consistency,
/// chart-vs-table ordering, missing dates staying null, composition gating and
/// the unavailable-source empty state. Uses a scripted fake provider - no DB,
/// no network, no fabricated production data.
/// </summary>
public class DailyPerformanceServiceTests
{
    private sealed class FakeShopTimeZone : IShopTimeZone
    {
        public string TimeZoneId => ShopTimeZoneOptions.DefaultTimeZoneId;
        public DateTimeOffset ToShopLocal(DateTimeOffset utcInstant) => utcInstant;
        public DateOnly GetShopLocalDate(DateTimeOffset utcInstant) => DateOnly.FromDateTime(utcInstant.Date);
        public DateOnly Today() => new(2026, 9, 22);
        public DateTime ToDateTime(DateOnly shopLocalDate) => shopLocalDate.ToDateTime(new TimeOnly(0, 0));
        public DateTime TodayMidnight() => ToDateTime(Today());
    }

    private sealed class FakeSource : IAccountDailyMetricSource
    {
        private readonly Func<AccountDailyMetricQuery, AccountDailyMetricResult> _handler;

        public FakeSource(Func<AccountDailyMetricQuery, AccountDailyMetricResult> handler) => _handler = handler;

        public List<AccountDailyMetricQuery> Queries { get; } = new();

        public Task<AccountDailyMetricResult> GetAsync(AccountDailyMetricQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(_handler(query));
        }
    }

    private static AccountDailyMetricRecord Record(DateTime date, long reach, long views, long profileViews,
        long likes, long comments, long shares, long gained, long lost)
        => new()
        {
            Date = date,
            Reach = reach,
            VideoViews = views,
            ProfileViews = profileViews,
            Likes = likes,
            Comments = comments,
            Shares = shares,
            FollowersGained = gained,
            FollowersLost = lost
        };

    private static DailyPerformanceService CreateService(IAccountDailyMetricSource source)
        => new(source, new FakeShopTimeZone());

    private static DailyReportFilter Filter(int startMonth, int startDay, int endMonth, int endDay, int year = 2026)
        => new() { StartDate = new DateTime(year, startMonth, startDay), EndDate = new DateTime(year, endMonth, endDay) };

    private static DailyReportMetric Card(DailyReportData data, string key)
        => data.Cards.Single(c => c.Key == key);

    // ------------------------------------------------------------------
    // Unavailable source (current production state): honest empty report
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnavailableSource_AllMetricsUnavailable_WithReason_NoFabricatedZeros()
    {
        var service = CreateService(new FakeSource(_ => AccountDailyMetricResult.Unavailable("blocked: no account-level source")));

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        Assert.False(data.HasAnyMetricData);
        Assert.All(data.Cards, card =>
        {
            Assert.Null(card.CurrentValue);
            Assert.Equal(DailyMetricStatus.Unavailable, card.CurrentStatus);
            Assert.False(card.IsAvailable);
            Assert.Contains("blocked", card.UnavailableReason);
        });
        Assert.All(data.Trend, row =>
        {
            Assert.False(row.HasRecord);
            Assert.Null(row.Reach);
            Assert.Null(row.NetFollower);
        });
        Assert.False(data.InteractionComposition.IsAvailable);
        Assert.False(data.ViewerComposition.IsAvailable);
        Assert.Contains(data.Notices, n => n.Contains("belum terintegrasi"));
    }

    [Fact]
    public async Task UnavailableSource_PreviousPeriodNotQueriedTwice()
    {
        var source = new FakeSource(_ => AccountDailyMetricResult.Unavailable("blocked"));
        var service = CreateService(source);

        await service.BuildAsync(Filter(8, 23, 9, 5));

        // One query only - asking an unanswerable provider twice is pointless.
        Assert.Single(source.Queries);
    }

    [Fact]
    public async Task UnavailableSource_NoticeSurfacesActualProviderReason()
    {
        // Proven 2026-10-21: a transport failure must not be reported as a generic
        // "not integrated" state - the page notice carries the provider's real
        // reason so the operator can tell a config gap from a network failure.
        const string reason =
            "Request TikTok Shop Analytics gagal (jaringan/transport) - data video views level akun belum dapat diambil.";
        var service = CreateService(new FakeSource(_ => AccountDailyMetricResult.Unavailable(reason)));

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        Assert.Contains(data.Notices, n => n.Contains(reason));
    }

    // ------------------------------------------------------------------
    // Full-coverage dataset: cards, deltas, ordering
    // ------------------------------------------------------------------

    private static FakeSource FullSource()
    {
        // 14-day current period 23 Aug - 5 Sep 2026 with complete data.
        return new FakeSource(query =>
        {
            var records = new List<AccountDailyMetricRecord>();
            for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
            {
                // Deterministic per-day values; day 1 gets likes=800 etc.
                var i = (day.Day % 7) + 1;
                records.Add(Record(day, reach: 1000 * i, views: 2000 * i, profileViews: 10 * i,
                    likes: 80 * i, comments: 8 * i, shares: 12 * i, gained: 10 * i, lost: 3 * i));
            }

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "fake-account-source",
                ViewerComposition = new AccountViewerComposition { NewViewers = 520, ReturningViewers = 480 }
            });
        });
    }

    [Fact]
    public async Task FullSource_Cards_MatchLockedFormulas()
    {
        var data = await CreateService(FullSource()).BuildAsync(Filter(8, 23, 9, 5));

        var reach = Card(data, DailyReportMetrics.Reach);
        Assert.True(reach.IsAvailable);
        Assert.Equal(DailyMetricStatus.Complete, reach.CurrentStatus);
        Assert.Equal(DailyMetricStatus.Complete, reach.PreviousStatus);

        // Interactions = likes + comments + shares per day, summed over the period.
        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(data.Trend.Sum(r => r.Interactions ?? 0L), interactions.CurrentValue!.Value);

        // Period ER = total interactions / total reach x 100 (not average of daily ERs).
        var er = Card(data, DailyReportMetrics.EngagementRate);
        var expectedEr = Math.Round(interactions.CurrentValue!.Value / reach.CurrentValue!.Value * 100m, 4);
        Assert.Equal(expectedEr, er.CurrentValue);
        Assert.Equal(DailyMetricDeltaKind.PercentagePoints, er.DeltaKind);

        // Net Follower = gained - lost.
        var net = Card(data, DailyReportMetrics.NetFollower);
        Assert.Equal(data.Trend.Sum(r => r.NetFollower ?? 0L), net.CurrentValue!.Value);
    }

    [Fact]
    public async Task FullSource_DifferenceAndGrowth_AreComputedForAbsoluteMetrics()
    {
        var data = await CreateService(FullSource()).BuildAsync(Filter(8, 23, 9, 5));

        var reach = Card(data, DailyReportMetrics.Reach);
        Assert.NotNull(reach.Difference);
        Assert.NotNull(reach.GrowthPercentage);
        Assert.Equal(
            DailyReportCalculator.GrowthPercentage(reach.CurrentValue, reach.PreviousValue),
            reach.GrowthPercentage);
    }

    [Fact]
    public async Task TotalFollower_WithoutSnapshot_IsNoData_NotZero()
    {
        var data = await CreateService(FullSource()).BuildAsync(Filter(8, 23, 9, 5));

        var total = Card(data, DailyReportMetrics.TotalFollower);
        Assert.Null(total.CurrentValue);
        Assert.Equal(DailyMetricStatus.NoData, total.CurrentStatus);
        Assert.False(total.ShowInComparison);
        Assert.Equal(DailyMetricDeltaKind.PositionOnly, total.DeltaKind);
    }

    [Fact]
    public async Task TotalFollower_UsesSnapshotInsidePeriod_NeverAfterEndDate()
    {
        var source = new FakeSource(query => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = new[]
            {
                Record(new DateTime(2026, 8, 23), 100, 200, 5, 8, 1, 1, 2, 0)
            },
            FollowerSnapshots = new[]
            {
                new FollowerSnapshot { AsOfDate = new DateTime(2026, 9, 1), FollowerCount = 20100 },
                new FollowerSnapshot { AsOfDate = new DateTime(2026, 9, 20), FollowerCount = 21000 } // after EndDate
            }
        }));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        var total = Card(data, DailyReportMetrics.TotalFollower);
        Assert.Equal(20100m, total.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, total.CurrentStatus);
    }

    [Fact]
    public async Task Trend_IsAscending_DailyRows_AreDescending_SameValues()
    {
        var data = await CreateService(FullSource()).BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(14, data.Trend.Count);
        Assert.Equal(14, data.DailyRows.Count);

        for (var i = 1; i < data.Trend.Count; i++)
        {
            Assert.True(data.Trend[i].Date > data.Trend[i - 1].Date, "Trend must be ascending");
        }

        for (var i = 1; i < data.DailyRows.Count; i++)
        {
            Assert.True(data.DailyRows[i].Date < data.DailyRows[i - 1].Date, "Daily table must be descending");
        }

        // Same rows, opposite order: every value must match exactly.
        Assert.Equal(new DateTime(2026, 8, 23), data.Trend.First().Date);
        Assert.Equal(new DateTime(2026, 9, 5), data.DailyRows.First().Date);

        var reversedTrend = data.Trend.Reverse().ToList();
        for (var i = 0; i < reversedTrend.Count; i++)
        {
            Assert.Equal(reversedTrend[i].Date, data.DailyRows[i].Date);
            Assert.Equal(reversedTrend[i].Reach, data.DailyRows[i].Reach);
            Assert.Equal(reversedTrend[i].Interactions, data.DailyRows[i].Interactions);
            Assert.Equal(reversedTrend[i].EngagementRate, data.DailyRows[i].EngagementRate);
            Assert.Equal(reversedTrend[i].NetFollower, data.DailyRows[i].NetFollower);
        }
    }

    [Fact]
    public async Task DailyRow_EngagementRate_IsInteractionsOverReach()
    {
        var data = await CreateService(FullSource()).BuildAsync(Filter(8, 23, 9, 5));

        foreach (var row in data.Trend.Where(r => r.HasRecord))
        {
            var expected = DailyReportCalculator.EngagementRate(row.Interactions, row.Reach);
            Assert.Equal(expected, row.EngagementRate);
        }
    }

    [Fact]
    public async Task Compositions_AreAvailable_WithLockedSegmentOrder()
    {
        var data = await CreateService(FullSource()).BuildAsync(Filter(8, 23, 9, 5));

        Assert.True(data.InteractionComposition.IsAvailable);
        Assert.Equal(new[] { "like", "comment", "share" }, data.InteractionComposition.Segments.Select(s => s.Key));
        Assert.Equal(100m, data.InteractionComposition.Segments.Sum(s => s.Percentage!.Value));

        Assert.True(data.ViewerComposition.IsAvailable);
        Assert.Equal(new[] { "new", "returning" }, data.ViewerComposition.Segments.Select(s => s.Key));
        Assert.Equal(52m, data.ViewerComposition.Segments.First(s => s.Key == "new").Percentage);
        Assert.Equal(48m, data.ViewerComposition.Segments.First(s => s.Key == "returning").Percentage);
    }

    // ------------------------------------------------------------------
    // Missing dates / partial data: null is null, never zero
    // ------------------------------------------------------------------

    [Fact]
    public async Task MissingDates_StayNull_BothInRowsAndSums()
    {
        // Records exist only on 23-24 Aug (2 of 14 days).
        var source = new FakeSource(query => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = new[]
            {
                Record(new DateTime(2026, 8, 23), 1000, 2000, 50, 800, 80, 120, 100, 30),
                Record(new DateTime(2026, 8, 24), 2000, 3000, 60, 900, 90, 130, 200, 40)
            }
        }));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        // Days without a record are null (never 0).
        var dayWithoutRecord = data.Trend.Single(r => r.Date == new DateTime(2026, 8, 30));
        Assert.False(dayWithoutRecord.HasRecord);
        Assert.Null(dayWithoutRecord.Reach);
        Assert.Null(dayWithoutRecord.VideoViews);
        Assert.Null(dayWithoutRecord.Interactions);
        Assert.Null(dayWithoutRecord.EngagementRate);
        Assert.Null(dayWithoutRecord.NetFollower);

        // Sums cover only the 2 recorded days and are flagged Partial.
        var reach = Card(data, DailyReportMetrics.Reach);
        Assert.Equal(3000m, reach.CurrentValue);
        Assert.Equal(DailyMetricStatus.Partial, reach.CurrentStatus);
        Assert.True(reach.IsPartial);
        Assert.NotNull(reach.CoverageNote);
        Assert.Contains("2", reach.CoverageNote);

        // Page-level notice warns about partial coverage.
        Assert.Contains(data.Notices, n => n.Contains("parsial"));
    }

    [Fact]
    public async Task DailyZero_IsDisplayedAsZero_NotAsMissing()
    {
        var source = new FakeSource(query => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = new[] { Record(new DateTime(2026, 8, 23), 0, 0, 0, 0, 0, 0, 0, 0) }
        }));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 23));

        var row = Assert.Single(data.Trend);
        Assert.True(row.HasRecord);
        Assert.Equal(0L, row.Reach);
        Assert.Equal(0L, row.Interactions);
        Assert.Equal(0L, row.NetFollower);
        Assert.Null(row.EngagementRate); // ER with 0 reach is undefined, not 0%

        var reach = Card(data, DailyReportMetrics.Reach);
        Assert.Equal(0m, reach.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, reach.CurrentStatus);

        // Period ER with total reach 0: unavailable, not a divide-by-zero.
        Assert.Null(Card(data, DailyReportMetrics.EngagementRate).CurrentValue);
        Assert.Equal(DailyMetricStatus.NoData, Card(data, DailyReportMetrics.EngagementRate).CurrentStatus);
    }

    [Fact]
    public async Task RecordsOutsideRequestedRange_AreIgnored()
    {
        var source = new FakeSource(query => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = new[]
            {
                Record(new DateTime(2026, 8, 20), 999, 999, 9, 9, 9, 9, 9, 9),   // before Start
                Record(new DateTime(2026, 8, 23), 100, 200, 5, 8, 1, 1, 2, 0),
                Record(new DateTime(2026, 8, 26), 888, 888, 8, 8, 8, 8, 8, 8)     // after End
            }
        }));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        Assert.Equal(100m, Card(data, DailyReportMetrics.Reach).CurrentValue);
        Assert.Equal(2, data.Trend.Count);
        Assert.DoesNotContain(data.Trend, r => r.Reach is 888 or 999);
    }

    [Fact]
    public async Task PartialInteractionComponents_BlockInteractionComposition()
    {
        // Likes present every day, comments/shares missing on some days ->
        // the interaction composition must NOT render (no misleading shares).
        var records = new List<AccountDailyMetricRecord>
        {
            Record(new DateTime(2026, 8, 23), 100, 100, 5, 80, 8, 12, 2, 0),
            new AccountDailyMetricRecord
            {
                Date = new DateTime(2026, 8, 24),
                Reach = 100,
                VideoViews = 100,
                ProfileViews = 5,
                Likes = 90
                // Comments / Shares null
            }
        };

        var source = new FakeSource(_ => AccountDailyMetricResult.Available(new AccountDailyMetricDataset { Records = records }));
        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        Assert.False(data.InteractionComposition.IsAvailable);
        Assert.All(data.InteractionComposition.Segments, s => Assert.Null(s.Percentage));
        Assert.Contains("tidak lengkap", data.InteractionComposition.UnavailableReason);
    }

    [Fact]
    public async Task MissingViewerComposition_ShowsEmptyState_NotFakeShares()
    {
        var data = await CreateService(FullSource()).BuildAsync(Filter(8, 23, 8, 23));

        // FullSource supplies ViewerComposition, so flip it with a source that does not.
        var source = new FakeSource(_ => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = new[] { Record(new DateTime(2026, 8, 23), 100, 100, 5, 8, 1, 1, 2, 0) }
        }));
        var data2 = await CreateService(source).BuildAsync(Filter(8, 23, 8, 23));

        Assert.False(data2.ViewerComposition.IsAvailable);
        Assert.Equal(2, data2.ViewerComposition.Segments.Count);
        Assert.All(data2.ViewerComposition.Segments, s => Assert.Null(s.Percentage));
        Assert.NotNull(data2.ViewerComposition.UnavailableReason);

        // Sanity: with composition present it renders.
        Assert.True(data.ViewerComposition.IsAvailable);
    }

    // ------------------------------------------------------------------
    // Query windows (half-open, shop-local dates)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Queries_UseHalfOpenBounds_ForBothPeriods()
    {
        var source = new FakeSource(_ => AccountDailyMetricResult.Unavailable("blocked"));
        var service = CreateService(source);

        await service.BuildAsync(Filter(8, 23, 9, 5));

        var current = Assert.Single(source.Queries);
        Assert.Equal(new DateTime(2026, 8, 23), current.Start);
        Assert.Equal(new DateTime(2026, 9, 6), current.EndExclusive);
    }

    [Fact]
    public async Task PeriodInfo_ReportsBothRanges_AndEqualDayCounts()
    {
        var data = await CreateService(FullSource()).BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(new DateTime(2026, 8, 23), data.CurrentStart);
        Assert.Equal(new DateTime(2026, 9, 5), data.CurrentEnd);
        Assert.Equal(new DateTime(2026, 8, 9), data.PreviousStart);
        Assert.Equal(new DateTime(2026, 8, 22), data.PreviousEnd);
        Assert.Equal(14, data.CurrentDayCount);
        Assert.Equal(14, data.PreviousDayCount);
        Assert.Equal("Asia/Jakarta", data.TimeZoneId);
        Assert.Equal("GMT+7", data.TimeZoneLabel);
    }

    [Fact]
    public async Task ComparisonRows_ExcludeTotalFollower_ContainTheSixDeltaMetrics()
    {
        var data = await CreateService(FullSource()).BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(6, data.ComparisonRows.Count);
        Assert.DoesNotContain(data.ComparisonRows, r => r.Key == DailyReportMetrics.TotalFollower);
        Assert.Equal(
            new[] { "reach", "video_views", "profile_views", "interactions", "engagement_rate", "net_follower" },
            data.ComparisonRows.Select(r => r.Key).ToArray());
    }

    [Fact]
    public async Task PreviousPeriodData_IsReadFromItsOwnQueryWindow()
    {
        var source = new FakeSource(query =>
        {
            // Previous period (9-22 Aug) gets bigger numbers than current.
            var isPrevious = query.Start < new DateTime(2026, 8, 23);
            var records = new List<AccountDailyMetricRecord>();
            for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
            {
                var value = isPrevious ? 2000L : 1000L;
                records.Add(Record(day, value, value * 2, 10, 80, 8, 12, 10, 3));
            }

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset { Records = records });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        var reach = Card(data, DailyReportMetrics.Reach);
        Assert.Equal(14000m, reach.CurrentValue);
        Assert.Equal(28000m, reach.PreviousValue);
        Assert.Equal(-14000m, reach.Difference);
        Assert.Equal(-50m, reach.GrowthPercentage);
    }

    // ------------------------------------------------------------------
    // Shop-video commerce funnel (Metrik Shop Video): own section only
    // ------------------------------------------------------------------

    private static DailyReportMetric ShopCard(DailyReportData data, string key)
        => data.ShopVideoMetrics.Single(c => c.Key == key);

    private static AccountDailyMetricRecord ShopRecord(DateTime date, long? impressions, long? clicks, long? orders = null)
        => new()
        {
            Date = date,
            Reach = 100,
            VideoViews = 500,
            ProfileViews = 5,
            Likes = 10,
            Comments = 1,
            Shares = 1,
            FollowersGained = 2,
            FollowersLost = 0,
            ProductImpressions = impressions,
            ProductClicks = clicks,
            VideoAttributedSkuOrders = orders
        };

    private static FakeSource ShopFunnelSource()
    {
        // Complete 14-day current + previous period, shop fields recorded every day.
        return new FakeSource(query =>
        {
            var records = new List<AccountDailyMetricRecord>();
            for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
            {
                var i = (day.Day % 7) + 1;
                records.Add(ShopRecord(day, impressions: 100 * i, clicks: 5 * i, orders: i));
            }

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "fake-shop-overview-source",
                AvailableMetricKeys = new HashSet<string>
                {
                    DailyReportMetrics.VideoViews,
                    DailyReportMetrics.ProductImpressions,
                    DailyReportMetrics.ProductClicks,
                    DailyReportMetrics.VideoAttributedSkuOrders
                }
            });
        });
    }

    [Fact]
    public async Task ShopVideoMetrics_RenderImpressionsAndClicks_WithPeriodSums_AndDeltas()
    {
        var data = await CreateService(ShopFunnelSource()).BuildAsync(Filter(8, 23, 9, 5));

        var impressions = ShopCard(data, DailyReportMetrics.ProductImpressions);
        Assert.True(impressions.IsAvailable);
        Assert.Equal(DailyMetricStatus.Complete, impressions.CurrentStatus);
        Assert.Equal(DailyMetricStatus.Complete, impressions.PreviousStatus);
        Assert.Equal(data.Trend.Sum(r => r.ProductImpressions ?? 0L), impressions.CurrentValue!.Value);
        Assert.Equal(data.DailyRows.Sum(r => r.ProductImpressions ?? 0L), impressions.CurrentValue!.Value);

        var clicks = ShopCard(data, DailyReportMetrics.ProductClicks);
        Assert.True(clicks.IsAvailable);
        Assert.Equal(DailyMetricStatus.Complete, clicks.CurrentStatus);
        Assert.Equal(data.Trend.Sum(r => r.ProductClicks ?? 0L), clicks.CurrentValue!.Value);
        Assert.NotNull(clicks.Difference);
        Assert.NotNull(clicks.GrowthPercentage);
        Assert.Equal(
            DailyReportCalculator.GrowthPercentage(clicks.CurrentValue, clicks.PreviousValue),
            clicks.GrowthPercentage);

        // Own section only: never part of the account summary cards / comparison table.
        Assert.DoesNotContain(data.Cards, c => c.Key == DailyReportMetrics.ProductImpressions);
        Assert.DoesNotContain(data.Cards, c => c.Key == DailyReportMetrics.ProductClicks);
        Assert.DoesNotContain(data.ComparisonRows, r => r.Key == DailyReportMetrics.ProductImpressions);
        Assert.DoesNotContain(data.ComparisonRows, r => r.Key == DailyReportMetrics.ProductClicks);

        // Video Views (existing behaviour) stays on the summary cards, untouched.
        Assert.True(Card(data, DailyReportMetrics.VideoViews).IsAvailable);
        Assert.True(data.HasAnyMetricData);
    }

    [Fact]
    public async Task ShopVideoMetrics_Unavailable_WhenSourceDoesNotDeclareThem_NeverLeaks()
    {
        // Verified-partial source: only Video Views declared - even though the records
        // physically carry shop fields, no value may surface for undeclared metrics.
        var source = new FakeSource(query => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = new[] { ShopRecord(new DateTime(2026, 8, 23), impressions: 438, clicks: 22) },
            SourceName = "video-views-only-source",
            AvailableMetricKeys = new HashSet<string> { DailyReportMetrics.VideoViews }
        }));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        foreach (var key in new[] { DailyReportMetrics.ProductImpressions, DailyReportMetrics.ProductClicks })
        {
            var metric = ShopCard(data, key);
            Assert.Null(metric.CurrentValue);
            Assert.Equal(DailyMetricStatus.Unavailable, metric.CurrentStatus);
            Assert.False(metric.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(metric.UnavailableReason));
        }

        Assert.All(data.Trend, row =>
        {
            Assert.Null(row.ProductImpressions);
            Assert.Null(row.ProductClicks);
        });

        // The declared metric keeps working - the shop extension does not break Video Views.
        Assert.True(Card(data, DailyReportMetrics.VideoViews).IsAvailable);
    }

    [Fact]
    public async Task ShopVideoMetrics_MissingDaysStayNull_PartialNeverZero()
    {
        var source = new FakeSource(_ => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = new[]
            {
                ShopRecord(new DateTime(2026, 8, 23), impressions: 438, clicks: 22),
                ShopRecord(new DateTime(2026, 8, 24), impressions: 100, clicks: null)
            }
        }));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        // Impressions recorded on BOTH days of the 2-day period -> Complete.
        var impressions = ShopCard(data, DailyReportMetrics.ProductImpressions);
        Assert.Equal(538m, impressions.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, impressions.CurrentStatus);
        Assert.False(impressions.IsPartial);

        // Clicks missing on day 2 -> Partial (never presented as a full-period total).
        var clicks = ShopCard(data, DailyReportMetrics.ProductClicks);
        Assert.Equal(22m, clicks.CurrentValue);
        Assert.Equal(DailyMetricStatus.Partial, clicks.CurrentStatus);
        Assert.True(clicks.IsPartial);

        // A recorded day without the field stays null (never 0).
        var dayWithoutClicks = data.Trend.Single(r => r.Date == new DateTime(2026, 8, 24));
        Assert.Equal(100L, dayWithoutClicks.ProductImpressions);
        Assert.Null(dayWithoutClicks.ProductClicks);

        Assert.True(dayWithoutClicks.HasRecord);

        // Days with no record at all stay null in both fields (never 0).
        Assert.All(data.Trend.Where(r => r.Date > new DateTime(2026, 8, 24)), row =>
        {
            Assert.False(row.HasRecord);
            Assert.Null(row.ProductImpressions);
            Assert.Null(row.ProductClicks);
        });
    }

    // ------------------------------------------------------------------
    // Period interactions (TikTok shop video list): windowed aggregates
    // ------------------------------------------------------------------

    private static FakeSource InteractionsSource(
        AccountPeriodInteractions current,
        AccountPeriodInteractions? previous,
        IReadOnlyCollection<string>? availableKeys,
        bool recordsCarryDailyInteractions = false)
    {
        return new FakeSource(query =>
        {
            var isPrevious = query.Start < new DateTime(2026, 8, 23);
            var records = new List<AccountDailyMetricRecord>();
            for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
            {
                // TikTok shop overview shape by default: daily video views only;
                // interactions exist solely as period aggregates.
                records.Add(new AccountDailyMetricRecord
                {
                    Date = day,
                    VideoViews = 100,
                    Likes = recordsCarryDailyInteractions ? 999 : null,
                    Comments = recordsCarryDailyInteractions ? 888 : null,
                    Shares = recordsCarryDailyInteractions ? 777 : null
                });
            }

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "tiktok-shop-video-overview",
                PeriodInteractions = isPrevious ? previous : current,
                AvailableMetricKeys = availableKeys is null
                    ? null
                    : new HashSet<string>(availableKeys)
            });
        });
    }

    [Fact]
    public async Task PeriodInteractions_SupplyCardCompositionAndDelta_DailyRowsStayNull()
    {
        var source = InteractionsSource(
            new AccountPeriodInteractions { Likes = 100, Comments = 50, Shares = 25, VideoCount = 12 },
            new AccountPeriodInteractions { Likes = 60, Comments = 20, Shares = 20, VideoCount = 9 },
            new[] { DailyReportMetrics.VideoViews, DailyReportMetrics.Interactions });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.True(interactions.IsAvailable);
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);
        Assert.Equal(175m, interactions.CurrentValue); // 100 + 50 + 25
        Assert.Equal(100m, interactions.PreviousValue); // 60 + 20 + 20
        Assert.Equal(75m, interactions.Difference);
        Assert.Equal(75m, interactions.GrowthPercentage);

        // A period aggregate is NEVER distributed across days: rows stay null.
        Assert.All(data.Trend, row => Assert.Null(row.Interactions));
        Assert.All(data.DailyRows, row => Assert.Null(row.Interactions));

        // Composition from the exact period totals, locked segment order.
        Assert.True(data.InteractionComposition.IsAvailable);
        Assert.Equal(175m, data.InteractionComposition.Total);
        Assert.Equal(new[] { "like", "comment", "share" },
            data.InteractionComposition.Segments.Select(s => s.Key));
        Assert.Equal(100m, data.InteractionComposition.Segments.Single(s => s.Key == "like").Value);
        Assert.Equal(50m, data.InteractionComposition.Segments.Single(s => s.Key == "comment").Value);
        Assert.Equal(25m, data.InteractionComposition.Segments.Single(s => s.Key == "share").Value);
        Assert.Equal(100m, data.InteractionComposition.Segments.Sum(s => s.Percentage!.Value));

        // ER keeps the locked formula: Reach is not supplied, so the card stays "-"
        // with an explicit reason - never swapped for Views.
        var er = Card(data, DailyReportMetrics.EngagementRate);
        Assert.Null(er.CurrentValue);
        Assert.Equal(DailyMetricStatus.Unavailable, er.CurrentStatus);
        Assert.Contains("Reach", er.UnavailableReason);

        // Video Views is untouched by the interactions flow.
        Assert.True(Card(data, DailyReportMetrics.VideoViews).IsAvailable);
    }

    [Fact]
    public async Task PeriodInteractions_AllZeros_AreValidZeros_NotMissing()
    {
        var source = InteractionsSource(
            new AccountPeriodInteractions { Likes = 0, Comments = 0, Shares = 0, VideoCount = 0 },
            null,
            new[] { DailyReportMetrics.VideoViews, DailyReportMetrics.Interactions });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.True(interactions.IsAvailable);
        Assert.Equal(0m, interactions.CurrentValue); // a real zero, not "-"
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);
        Assert.Null(interactions.PreviousValue);

        // A 0/0/0 donut would be meaningless: the composition honestly stays empty
        // with a reason instead of rendering fabricated 33/33/33 shares.
        Assert.False(data.InteractionComposition.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(data.InteractionComposition.UnavailableReason));
        // Slots are locked, but NO percentage may ever be derived from it.
        Assert.All(data.InteractionComposition.Segments, s => Assert.Null(s.Percentage));
    }

    [Fact]
    public async Task UndeclaredInteractions_StayUnavailable_EvenWhenPeriodTotalsExist()
    {
        // Verified-partial source that declares only Video Views: period interaction
        // totals and daily likes/comments/shares on records must both stay hidden.
        var source = InteractionsSource(
            new AccountPeriodInteractions { Likes = 100, Comments = 50, Shares = 25, VideoCount = 12 },
            null,
            new[] { DailyReportMetrics.VideoViews },
            recordsCarryDailyInteractions: true);

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Null(interactions.CurrentValue);
        Assert.Equal(DailyMetricStatus.Unavailable, interactions.CurrentStatus);
        Assert.False(interactions.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(interactions.UnavailableReason));

        Assert.False(data.InteractionComposition.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(data.InteractionComposition.UnavailableReason));

        Assert.All(data.Trend, row => Assert.Null(row.Interactions));

        var er = Card(data, DailyReportMetrics.EngagementRate);
        Assert.Null(er.CurrentValue);
        Assert.Contains("Reach", er.UnavailableReason);

        // Declared metric keeps working.
        Assert.True(Card(data, DailyReportMetrics.VideoViews).IsAvailable);
    }

    [Fact]
    public async Task PartialPeriodInteractionFields_NeverPresentACompleteTotal()
    {
        // Only likes present (comments/shares fields absent from the API response):
        // the sum of one component is NOT the Interactions total.
        var source = InteractionsSource(
            new AccountPeriodInteractions { Likes = 100, Comments = null, Shares = null, VideoCount = 5 },
            null,
            new[] { DailyReportMetrics.VideoViews, DailyReportMetrics.Interactions });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Null(interactions.CurrentValue); // no fabricated total from one component
        Assert.Equal(DailyMetricStatus.NoData, interactions.CurrentStatus);

        Assert.False(data.InteractionComposition.IsAvailable);
    }

    [Fact]
    public async Task EngagementRate_UsesPeriodInteractionsOverPeriodReach()
    {
        // Full provider: records carry Reach AND daily interactions that DIFFER from
        // the period totals - the period totals must win (verified windowed sums).
        var source = new FakeSource(query =>
        {
            var records = new List<AccountDailyMetricRecord>();
            for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
            {
                records.Add(Record(day, reach: 100, views: 500, profileViews: 5,
                    likes: 5, comments: 1, shares: 1, gained: 2, lost: 0));
            }

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                PeriodInteractions = new AccountPeriodInteractions { Likes = 40, Comments = 10, Shares = 10, VideoCount = 7 }
            });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(60m, interactions.CurrentValue); // period totals (daily sum would be 24)

        var er = Card(data, DailyReportMetrics.EngagementRate);
        // 60 / 200 x 100 = 30%.
        Assert.Equal(30m, er.CurrentValue);
        Assert.Equal(DailyMetricDeltaKind.PercentagePoints, er.DeltaKind);
    }

    // ------------------------------------------------------------------
    // Shop-video analytics extras (verified overview-interval fields):
    // CTR/CTOR/GPM recomputed from period SUMS, GMV subtypes per-subtype only,
    // avg customers = mean of covered days, viewers = daily accumulation.
    // ------------------------------------------------------------------

    private static AccountDailyMetricRecord AnalyticsRecord(
        DateTime date,
        long views,
        long? impressions,
        long? clicks,
        long? orders,
        decimal? attributedGmv,
        decimal? videoGmv,
        decimal? indirectGmv,
        decimal? gpm,
        string currency = "IDR",
        decimal? ctr = null,
        decimal? ctor = null,
        long? viewers = null,
        decimal? adc = null)
        => new()
        {
            Date = date,
            VideoViews = views,
            ProductImpressions = impressions,
            ProductClicks = clicks,
            VideoAttributedSkuOrders = orders,
            VideoAttributedGmvAmount = attributedGmv,
            VideoAttributedGmvCurrency = attributedGmv is null ? null : currency,
            VideoGmvAmount = videoGmv,
            VideoGmvCurrency = videoGmv is null ? null : currency,
            VideoIndirectGmvAmount = indirectGmv,
            VideoIndirectGmvCurrency = indirectGmv is null ? null : currency,
            GpmAmount = gpm,
            GpmCurrency = gpm is null ? null : currency,
            Ctr = ctr,
            CtorSkuOrder = ctor,
            VideoProductViewers = viewers,
            AvgDailyCustomers = adc
        };

    private static readonly string[] AllShopAnalyticsKeys =
    {
        DailyReportMetrics.VideoViews,
        DailyReportMetrics.ProductImpressions,
        DailyReportMetrics.ProductClicks,
        DailyReportMetrics.VideoAttributedSkuOrders,
        DailyReportMetrics.ProductCtr,
        DailyReportMetrics.CtorSkuOrder,
        DailyReportMetrics.VideoAttributedGmv,
        DailyReportMetrics.VideoGmv,
        DailyReportMetrics.VideoIndirectGmv,
        DailyReportMetrics.Gpm,
        DailyReportMetrics.VideoProductViewers,
        DailyReportMetrics.AvgDailyCustomers
    };

    [Fact]
    public async Task ShopVideoAnalytics_PeriodValues_RecomputeRatesFromSums_NeverAverageDailyRates()
    {
        // Two days with DIFFERENT daily rates so a naive average would differ from the
        // correct sum-based recomputation (CTR: avg 15% vs sum-based 16.67%).
        var source = new FakeSource(query =>
        {
            var records = new List<AccountDailyMetricRecord>();
            for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
            {
                var first = day == query.Start;
                records.Add(AnalyticsRecord(day,
                    views: first ? 1000 : 2000,
                    impressions: first ? 100 : 200,
                    clicks: first ? 10 : 40,       // daily ctr 10% vs 20%
                    orders: first ? 1 : 3,         // daily ctor 10% vs 7.5%
                    attributedGmv: first ? 1000 : 2000,
                    videoGmv: first ? 800 : 1500,
                    indirectGmv: first ? 200 : 500,
                    gpm: first ? 1000 : 1000,
                    ctr: first ? 0.10m : 0.20m,
                    ctor: first ? 0.10m : 0.075m,
                    viewers: first ? 50 : 60,
                    adc: first ? 2 : 4));
            }

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "fake-shop-overview-source",
                AvailableMetricKeys = new HashSet<string>(AllShopAnalyticsKeys)
            });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24)); // 2 days

        // CTR = (10+40)/(100+200) x 100 = 16.666.., NOT (10%+20%)/2 = 15%.
        var ctr = ShopCard(data, DailyReportMetrics.ProductCtr);
        Assert.True(ctr.IsAvailable);
        Assert.Equal(50m / 300m * 100m, ctr.CurrentValue!.Value);
        Assert.Equal(DailyMetricDeltaKind.PercentagePoints, ctr.DeltaKind);

        // CTOR = (1+3)/(10+40) x 100 = 8%, NOT (10%+7.5%)/2 = 8.75%.
        var ctor = ShopCard(data, DailyReportMetrics.CtorSkuOrder);
        Assert.Equal(4m / 50m * 100m, ctor.CurrentValue!.Value);
        Assert.Equal(DailyMetricDeltaKind.PercentagePoints, ctor.DeltaKind);

        // GPM = 3000 / 3000 x 1000 = 1000 (currency preserved).
        var gpm = ShopCard(data, DailyReportMetrics.Gpm);
        Assert.Equal(1000m, gpm.CurrentValue);
        Assert.Equal("IDR", gpm.Currency);

        // GMV subtypes summed PER SUBTYPE only, never combined.
        Assert.Equal(3000m, ShopCard(data, DailyReportMetrics.VideoAttributedGmv).CurrentValue);
        Assert.Equal("IDR", ShopCard(data, DailyReportMetrics.VideoAttributedGmv).Currency);
        Assert.Equal(2300m, ShopCard(data, DailyReportMetrics.VideoGmv).CurrentValue);
        Assert.Equal(700m, ShopCard(data, DailyReportMetrics.VideoIndirectGmv).CurrentValue);

        // Viewers accumulate per day; avg customers = mean over covered days.
        Assert.Equal(110m, ShopCard(data, DailyReportMetrics.VideoProductViewers).CurrentValue);
        Assert.Equal(3m, ShopCard(data, DailyReportMetrics.AvgDailyCustomers).CurrentValue);

        // Daily rows carry the VERIFIED per-day values as percent points + currency.
        var day1 = data.DailyRows.Single(r => r.Date == Filter(8, 23, 8, 24).StartDate);
        Assert.Equal(10m, day1.ProductCtrPercent);
        Assert.Equal(10m, day1.CtorSkuOrderPercent);
        Assert.Equal(1000m, day1.GpmAmount);
        Assert.Equal("IDR", day1.GpmCurrency);
        Assert.Equal(1000m, day1.VideoAttributedGmvAmount);
        Assert.Equal(50L, day1.VideoProductViewers);
        Assert.Equal(2m, day1.AvgDailyCustomers);
    }

    [Fact]
    public async Task ShopVideoAnalytics_MixedCurrencies_AreNeverSummed()
    {
        var source = new FakeSource(query =>
        {
            var records = new List<AccountDailyMetricRecord>
            {
                AnalyticsRecord(query.Start, views: 100, impressions: 10, clicks: 1, orders: 1,
                    attributedGmv: 500, videoGmv: 500, indirectGmv: 0, gpm: 100, currency: "IDR"),
                AnalyticsRecord(query.Start.AddDays(1), views: 100, impressions: 10, clicks: 1, orders: 1,
                    attributedGmv: 5, videoGmv: 5, indirectGmv: 0, gpm: 1, currency: "USD")
            };

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "fake-shop-overview-source",
                AvailableMetricKeys = new HashSet<string>(AllShopAnalyticsKeys)
            });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        var gmv = ShopCard(data, DailyReportMetrics.VideoAttributedGmv);
        Assert.Null(gmv.CurrentValue); // amounts in different currencies are never added
        Assert.Equal(DailyMetricStatus.NoData, gmv.CurrentStatus);
        Assert.Contains("mata uang", gmv.UnavailableReason, StringComparison.OrdinalIgnoreCase);

        // GPM inherits the same refusal (its numerator is the attributed GMV sum).
        var gpm = ShopCard(data, DailyReportMetrics.Gpm);
        Assert.Null(gpm.CurrentValue);
    }

    [Fact]
    public async Task ShopVideoAnalytics_MissingDays_MarkPartialWithCoverage()
    {
        var source = new FakeSource(query =>
        {
            var records = new List<AccountDailyMetricRecord>
            {
                AnalyticsRecord(query.Start, views: 1000, impressions: 100, clicks: 10, orders: 2,
                    attributedGmv: 1000, videoGmv: 800, indirectGmv: 200, gpm: 1000,
                    viewers: 40, adc: 2),
                // second day: NO record at all
            };

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "fake-shop-overview-source",
                AvailableMetricKeys = new HashSet<string>(AllShopAnalyticsKeys)
            });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        var gmv = ShopCard(data, DailyReportMetrics.VideoAttributedGmv);
        Assert.Equal(1000m, gmv.CurrentValue);
        Assert.Equal(DailyMetricStatus.Partial, gmv.CurrentStatus);
        Assert.True(gmv.IsPartial);
        Assert.Contains("1 dari 2", gmv.CoverageNote);

        var ctr = ShopCard(data, DailyReportMetrics.ProductCtr);
        Assert.Equal(DailyMetricStatus.Partial, ctr.CurrentStatus);
        Assert.Equal(10m, ctr.CurrentValue); // 10/100 x 100

        var adc = ShopCard(data, DailyReportMetrics.AvgDailyCustomers);
        Assert.Equal(2m, adc.CurrentValue); // mean over the ONE covered day
        Assert.Equal(DailyMetricStatus.Partial, adc.CurrentStatus);
    }

    [Fact]
    public async Task ShopVideoAnalytics_ZeroDenominator_NeverFabricatesRate()
    {
        var source = new FakeSource(query =>
        {
            var records = new List<AccountDailyMetricRecord>
            {
                AnalyticsRecord(query.Start, views: 0, impressions: 0, clicks: 0, orders: 0,
                    attributedGmv: 0, videoGmv: 0, indirectGmv: 0, gpm: 0)
            };

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "fake-shop-overview-source",
                AvailableMetricKeys = new HashSet<string>(AllShopAnalyticsKeys)
            });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 23));

        var ctr = ShopCard(data, DailyReportMetrics.ProductCtr);
        Assert.Null(ctr.CurrentValue);
        Assert.Contains("Product Impressions", ctr.UnavailableReason);

        var ctor = ShopCard(data, DailyReportMetrics.CtorSkuOrder);
        Assert.Null(ctor.CurrentValue);
        Assert.Contains("Product Clicks", ctor.UnavailableReason);

        var gpm = ShopCard(data, DailyReportMetrics.Gpm);
        Assert.Null(gpm.CurrentValue);
        Assert.Contains("Video Views", gpm.UnavailableReason);

        // Amounts with an explicit zero are real values, not "no data".
        Assert.Equal(0m, ShopCard(data, DailyReportMetrics.VideoAttributedGmv).CurrentValue);
    }

    [Fact]
    public async Task ShopVideoAnalytics_UndeclaredBySource_AllNewMetricsUnavailable()
    {
        var source = new FakeSource(query => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = new List<AccountDailyMetricRecord>
            {
                AnalyticsRecord(query.Start, views: 100, impressions: 10, clicks: 1, orders: 1,
                    attributedGmv: 100, videoGmv: 100, indirectGmv: 0, gpm: 100, viewers: 5, adc: 1)
            },
            SourceName = "views-only-source",
            AvailableMetricKeys = new HashSet<string> { DailyReportMetrics.VideoViews }
        }));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 23));

        foreach (var key in new[]
        {
            DailyReportMetrics.ProductCtr, DailyReportMetrics.CtorSkuOrder,
            DailyReportMetrics.VideoAttributedGmv, DailyReportMetrics.VideoGmv,
            DailyReportMetrics.VideoIndirectGmv, DailyReportMetrics.Gpm,
            DailyReportMetrics.VideoProductViewers, DailyReportMetrics.AvgDailyCustomers
        })
        {
            var card = ShopCard(data, key);
            Assert.False(card.IsAvailable);
            Assert.Null(card.CurrentValue);
            Assert.Equal(DailyMetricStatus.Unavailable, card.CurrentStatus);
        }

        var row = Assert.Single(data.DailyRows);
        Assert.Null(row.ProductCtrPercent);
        Assert.Null(row.VideoAttributedGmvAmount);
        Assert.Null(row.GpmAmount);
        Assert.Null(row.VideoProductViewers);
        Assert.Null(row.AvgDailyCustomers);
    }

    // ------------------------------------------------------------------
    // Daily interactions from a batch breakdown attached to the records
    // (never a period total spread across days)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DailyInteractions_FromRecords_PerDayRowsAndPeriodSum()
    {
        var source = new FakeSource(query =>
        {
            var day1 = query.Start;
            var day2 = query.Start.AddDays(1);
            var records = new List<AccountDailyMetricRecord>
            {
                new() { Date = day1, VideoViews = 100, Likes = 5, Comments = 1, Shares = 1 },
                new() { Date = day2, VideoViews = 100, Likes = 2, Comments = 0, Shares = 3 }
            };

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "fake-daily-breakdown-source",
                AvailableMetricKeys = new HashSet<string> { DailyReportMetrics.VideoViews, DailyReportMetrics.Interactions }
            });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        // Daily rows show the REAL per-day values (7 and 5), not the period total each.
        var rows = data.DailyRows.OrderBy(r => r.Date).ToList();
        Assert.Equal(7, rows[0].Interactions);
        Assert.Equal(5, rows[1].Interactions);

        // Period = sum of the daily breakdown (Complete: every day has the breakdown).
        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(12, interactions.CurrentValue!.Value);
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);

        // Composition derives from the same per-day components.
        Assert.True(data.InteractionComposition.IsAvailable);
        var like = data.InteractionComposition.Segments.Single(s => s.Key == "like");
        Assert.Equal(Math.Round(7m / 12m * 100m, 4), like.Percentage!.Value); // calculator rounds to 4dp
    }

    [Fact]
    public async Task DailyInteractions_DayWithoutBreakdown_StaysNull_AndPartialIsHonest()
    {
        var source = new FakeSource(query =>
        {
            var records = new List<AccountDailyMetricRecord>
            {
                new() { Date = query.Start, VideoViews = 100, Likes = 5, Comments = 1, Shares = 1 },
                new() { Date = query.Start.AddDays(1), VideoViews = 100 } // no breakdown that day
            };

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records,
                SourceName = "fake-daily-breakdown-source",
                AvailableMetricKeys = new HashSet<string> { DailyReportMetrics.VideoViews, DailyReportMetrics.Interactions }
            });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 24));

        var rows = data.DailyRows.OrderBy(r => r.Date).ToList();
        Assert.Equal(7, rows[0].Interactions);
        Assert.Null(rows[1].Interactions); // honest gap, never 0

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(7, interactions.CurrentValue!.Value);
        Assert.Equal(DailyMetricStatus.Partial, interactions.CurrentStatus);
        Assert.Contains("1 dari 2", interactions.CoverageNote);

        // A partial denominator never produces a composition.
        Assert.False(data.InteractionComposition.IsAvailable);
    }
}
