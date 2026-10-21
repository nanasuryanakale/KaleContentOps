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
}
