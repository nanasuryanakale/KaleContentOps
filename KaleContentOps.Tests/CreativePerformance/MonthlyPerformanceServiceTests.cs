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
/// Service-level behaviour of the Monthly Report: calendar-month bucket queries
/// (sequential, clipped to the selected range), additive aggregation with honest
/// partial / no-data / unavailable states, windowed-interaction totals (never
/// distributed, never double counted), period comparison parity with the Daily
/// and Weekly Reports (total Interactions / total Reach x 100 - never the average
/// of monthly ERs), and card / chart / table reconciliation from one set of month
/// results. Uses a scripted fake provider - no DB, no network, no fabricated
/// production data.
/// </summary>
public class MonthlyPerformanceServiceTests
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
        public int MaxConcurrency { get; private set; }
        private int _active;

        public Task<AccountDailyMetricResult> GetAsync(AccountDailyMetricQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);

            // Concurrency probe: the service must read month windows sequentially.
            var active = Interlocked.Increment(ref _active);
            MaxConcurrency = Math.Max(MaxConcurrency, active);
            try
            {
                return Task.FromResult(_handler(query));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static readonly IReadOnlySet<string> ViewsAndInteractionsKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            DailyReportMetrics.VideoViews,
            DailyReportMetrics.Interactions
        };

    private static readonly IReadOnlySet<string> AllAggKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            DailyReportMetrics.Reach,
            DailyReportMetrics.VideoViews,
            DailyReportMetrics.Interactions,
            DailyReportMetrics.NetFollower,
            DailyReportMetrics.TotalFollower
        };

    private static MonthlyReportFilter Filter(int startMonth, int startDay, int endMonth, int endDay, int startYear = 2026, int endYear = 2026)
        => new() { StartDate = new DateTime(startYear, startMonth, startDay), EndDate = new DateTime(endYear, endMonth, endDay) };

    private static MonthlyPerformanceService CreateService(IAccountDailyMetricSource source)
        => new(source, new FakeShopTimeZone());

    private static DailyReportMetric Card(MonthlyReportData data, string key)
        => data.Cards.Single(c => c.Key == key);

    private static AccountPeriodInteractions Windowed(long? likes, long? comments, long? shares)
        => new() { Likes = likes, Comments = comments, Shares = shares };

    /// <summary>
    /// One record per calendar day of the half-open query window, with a
    /// deterministic Video Views value (also mirrored into Reach so a dataset
    /// that DECLARES Reach actually carries a value); optional follower fields.
    /// </summary>
    private static AccountDailyMetricResult Dataset(
        AccountDailyMetricQuery query,
        Func<DateTime, long?> viewsOn,
        IReadOnlySet<string>? availableKeys,
        AccountPeriodInteractions? windowedInteractions = null,
        bool includeRecords = true,
        Func<DateTime, (long? Gained, long? Lost)>? followersOn = null,
        IReadOnlyList<FollowerSnapshot>? snapshots = null,
        AccountViewerComposition? viewerComposition = null,
        Func<DateTime, (long? Likes, long? Comments, long? Shares)>? interactionsOn = null)
    {
        var records = new List<AccountDailyMetricRecord>();
        if (includeRecords)
        {
            for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
            {
                var (gained, lost) = followersOn?.Invoke(day) ?? (null, null);
                var (likes, comments, shares) = interactionsOn?.Invoke(day) ?? (0, 0, 0);
                var views = viewsOn(day);
                records.Add(new AccountDailyMetricRecord
                {
                    Date = day,
                    Reach = views,
                    VideoViews = views,
                    Likes = likes,
                    Comments = comments,
                    Shares = shares,
                    FollowersGained = gained,
                    FollowersLost = lost
                });
            }
        }

        return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = records,
            FollowerSnapshots = snapshots?.ToList() ?? new List<FollowerSnapshot>(),
            ViewerComposition = viewerComposition,
            SourceName = "fake-overview",
            AvailableMetricKeys = availableKeys,
            PeriodInteractions = windowedInteractions
        });
    }

    // ------------------------------------------------------------------
    // Query windows (clipped buckets, sequential, equal-length previous)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Queries_EachMonthWindow_IsClippedToTheSelectedRange_AndSequential()
    {
        var source = new FakeSource(q => Dataset(q, day => day.Day, ViewsAndInteractionsKeys, Windowed(1, 1, 1)));
        var service = CreateService(source);

        await service.BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(1, source.MaxConcurrency); // sequential, never parallel
        Assert.Equal(3, source.Queries.Count);  // 2 current month buckets + 1 previous-period bucket

        // Current period months, clipped at BOTH ends of the range.
        Assert.Equal(new DateTime(2026, 8, 23), source.Queries[0].Start);  // not the 1st of August
        Assert.Equal(new DateTime(2026, 9, 1), source.Queries[0].EndExclusive);
        Assert.Equal(new DateTime(2026, 9, 1), source.Queries[1].Start);
        Assert.Equal(new DateTime(2026, 9, 6), source.Queries[1].EndExclusive); // End + 1 day

        // Previous period = equal length (14 days), ending the day before the
        // current start. It is ONE calendar month (9-22 August), so exactly one
        // bucket window is queried for it.
        Assert.Equal(new DateTime(2026, 8, 9), source.Queries[2].Start);   // 14 days before 23 Aug
        Assert.Equal(new DateTime(2026, 8, 23), source.Queries[2].EndExclusive);
        Assert.All(source.Queries, q => Assert.True(q.Start <= q.EndExclusive));
    }

    [Fact]
    public async Task Periods_AreEqualLength_AndNeverOverlap()
    {
        var source = new FakeSource(q => Dataset(q, day => day.Day, ViewsAndInteractionsKeys, Windowed(1, 1, 1)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(14, data.CurrentDayCount);
        Assert.Equal(14, data.PreviousDayCount);
        Assert.Equal(new DateTime(2026, 8, 23), data.CurrentStart);
        Assert.Equal(new DateTime(2026, 9, 5), data.CurrentEnd);
        Assert.Equal(new DateTime(2026, 8, 9), data.PreviousStart);
        Assert.Equal(new DateTime(2026, 8, 22), data.PreviousEnd);
        Assert.True(data.PreviousEnd < data.CurrentStart); // no overlap
    }

    // ------------------------------------------------------------------
    // Aggregation: full month / partial month, sum over clipped days
    // ------------------------------------------------------------------

    [Fact]
    public async Task PartialMonths_SumOnlyDaysInsideTheRange_AndHonestTrendOrder()
    {
        // Video Views = day-of-month, so August contributes 23..31 (=243)
        // and September contributes 1..5 (=15): total 258.
        var source = new FakeSource(q => Dataset(q, day => day.Day, ViewsAndInteractionsKeys, Windowed(1, 1, 1)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(258m, card.CurrentValue);
        Assert.True(card.IsAvailable);
        Assert.Equal(DailyMetricStatus.Complete, card.CurrentStatus); // both partial months fully covered
        Assert.Equal(217m, card.PreviousValue);                       // 9..22 August = 217
        Assert.Equal(41m, card.Difference);
        Assert.Equal(DailyReportCalculator.GrowthPercentage(258m, 217m), card.GrowthPercentage);

        // Chart series (ascending) vs detail table (descending) vs KPI.
        Assert.Equal(2, data.Trend.Count);
        Assert.Equal(new DateTime(2026, 8, 23), data.Trend[0].Start);
        Assert.Equal(243, data.Trend[0].VideoViews);
        Assert.Equal(new DateTime(2026, 9, 1), data.Trend[1].Start);
        Assert.Equal(15, data.Trend[1].VideoViews);
        Assert.Equal(9, data.Trend[0].DayCount);
        Assert.Equal(5, data.Trend[1].DayCount);
        Assert.All(data.Trend, r => Assert.True(r.IsPartial));

        Assert.Equal(2, data.MonthlyRows.Count);
        Assert.Equal(new DateTime(2026, 9, 1), data.MonthlyRows[0].Start); // newest month first in the table
        Assert.Equal(new DateTime(2026, 8, 23), data.MonthlyRows[1].Start);
        Assert.Equal(data.Trend.Sum(r => r.VideoViews ?? 0), (long?)card.CurrentValue); // KPI == sum of chart
    }

    // ------------------------------------------------------------------
    // Interactions + Engagement Rate
    // ------------------------------------------------------------------

    [Fact]
    public async Task EngagementRate_IsTotalInteractionsOverTotalReach_NeverTheAverageOfMonthlyRates()
    {
        // Every month window: Reach = 1000/day, windowed interactions = 400.
        // August bucket = 9 days (9000 Reach, ER 4.4444%), September = 5 days
        // (5000 Reach, ER 8%). Period ER = 800 / 14000 = 5.7143% - NOT the
        // 6.2222% average of the two monthly rates.
        var source = new FakeSource(q => Dataset(q, _ => 1000, AllAggKeys, Windowed(200, 100, 100)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var reach = Card(data, DailyReportMetrics.Reach);
        Assert.Equal(14000m, reach.CurrentValue);

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(800m, interactions.CurrentValue); // 400 per month window, never spread across days

        var er = Card(data, DailyReportMetrics.EngagementRate);
        Assert.Equal(5.7143m, er.CurrentValue);
        Assert.Equal(DailyMetricDeltaKind.PercentagePoints, er.DeltaKind);

        // Per-month chart values keep the month-level rate for reference.
        Assert.Equal(4.4444m, data.Trend[0].EngagementRate); // 400 / 9000
        Assert.Equal(8m, data.Trend[1].EngagementRate);      // 400 / 5000
    }

    [Fact]
    public async Task Interactions_UsesWindowedTotals_AndRendersComposition()
    {
        var source = new FakeSource(q => Dataset(q, _ => 0, ViewsAndInteractionsKeys, Windowed(60, 30, 10)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        // The windowed totals cover exactly each queried month bucket (disjoint
        // days), so the period sums them once: 2 windows x (60 like, 30 comment,
        // 10 share) = 120 / 60 / 20 = 200 interactions - never spread to days,
        // never double counted. Percentages keep the 60/30/10 split against the
        // period total of 200.
        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(200m, interactions.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);

        var composition = data.InteractionComposition;
        Assert.True(composition.IsAvailable);
        Assert.Equal(200m, composition.Total);
        Assert.Equal(composition.Total, composition.Segments.Sum(s => s.Value)); // Like + Comment + Share = Interactions
        Assert.Collection(
            composition.Segments,
            s => { Assert.Equal("like", s.Key); Assert.Equal(120m, s.Value); Assert.Equal(60m, s.Percentage); },
            s => { Assert.Equal("comment", s.Key); Assert.Equal(60m, s.Value); Assert.Equal(30m, s.Percentage); },
            s => { Assert.Equal("share", s.Key); Assert.Equal(20m, s.Value); Assert.Equal(10m, s.Percentage); });
    }

    [Fact]
    public async Task Interactions_WindowedMissingASum_FallsBackToDaily_NoPartialTotal()
    {
        // Windowed total without shares is not a verified complete total, so the
        // daily values (all zero here) decide - never a partial windowed sum.
        var source = new FakeSource(q => Dataset(q, _ => 0, ViewsAndInteractionsKeys, Windowed(60, 30, null)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        // Daily components are real zeros, so the sum is a real zero - Complete.
        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(0m, interactions.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);
    }

    [Fact]
    public async Task Interactions_NotDeclaredByProvider_IsUnavailable_NeverZero()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { DailyReportMetrics.VideoViews };
        var source = new FakeSource(q => Dataset(q, day => day.Day, keys, Windowed(60, 30, 10)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Null(interactions.CurrentValue);
        Assert.Equal(DailyMetricStatus.Unavailable, interactions.CurrentStatus);
        Assert.False(interactions.IsAvailable);

        // Composition must not render from undeclared metrics either.
        Assert.False(data.InteractionComposition.IsAvailable);
        Assert.Empty(data.InteractionComposition.Segments);

        // Reach is carried by the records but NOT declared - it stays unavailable.
        Assert.Equal(DailyMetricStatus.Unavailable, Card(data, DailyReportMetrics.Reach).CurrentStatus);
    }

    // ------------------------------------------------------------------
    // Net Follower: positive / negative / zero
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(10, 4, 84)]    // +6/day  x 14 days
    [InlineData(1, 10, -126)]  // -9/day x 14 days
    [InlineData(5, 5, 0)]      // net zero is a REAL zero, never a null
    public async Task NetFollower_SumsGainedMinusLost_PeriodTotal(long gained, long lost, long expected)
    {
        var source = new FakeSource(q => Dataset(
            q, _ => 0, AllAggKeys, Windowed(0, 0, 0),
            followersOn: _ => (gained, lost)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var card = Card(data, DailyReportMetrics.NetFollower);
        Assert.Equal(expected, card.CurrentValue);
        Assert.True(card.IsAvailable); // zero is available data, unlike null
    }

    [Fact]
    public async Task NetFollower_NotDeclared_IsUnavailable_EvenWhenRecordsCarryValues()
    {
        var source = new FakeSource(q => Dataset(
            q, _ => 0, ViewsAndInteractionsKeys, Windowed(1, 1, 1),
            followersOn: _ => (10, 4)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(DailyMetricStatus.Unavailable, Card(data, DailyReportMetrics.NetFollower).CurrentStatus);
        Assert.Null(Card(data, DailyReportMetrics.NetFollower).CurrentValue);
    }

    // ------------------------------------------------------------------
    // NULL vs zero, NoData, partial coverage
    // ------------------------------------------------------------------

    [Fact]
    public async Task AllZeroDays_AreValidZeros_NotMissingValues()
    {
        var source = new FakeSource(q => Dataset(q, _ => 0, ViewsAndInteractionsKeys, Windowed(0, 0, 0)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(0m, card.CurrentValue);
        Assert.True(card.IsAvailable);
        Assert.Equal(DailyMetricStatus.Complete, card.CurrentStatus);
        Assert.True(data.HasAnyMetricData);
    }

    [Fact]
    public async Task AvailableSourceWithoutRecords_IsNoData_NullIsNotZero()
    {
        var source = new FakeSource(q => Dataset(q, _ => 0, ViewsAndInteractionsKeys, windowedInteractions: null, includeRecords: false));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Null(card.CurrentValue);            // NULL, never a fabricated 0
        Assert.Equal(DailyMetricStatus.NoData, card.CurrentStatus);
        Assert.False(card.IsAvailable);
        Assert.False(data.HasAnyMetricData);

        // The month rows still exist (honest "-" states), charts plot nothing.
        Assert.Equal(2, data.Trend.Count);
        Assert.All(data.Trend, r => Assert.False(r.HasAnyValue));
    }

    [Fact]
    public async Task MonthWithoutRecords_SumsTheOtherMonths_AndFlagsPartial()
    {
        // Only September carries records; August answers with an empty dataset.
        var source = new FakeSource(q => Dataset(
            q, day => day.Day, ViewsAndInteractionsKeys, Windowed(1, 1, 1),
            includeRecords: q.Start < new DateTime(2026, 9, 1) ? false : true));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(15m, card.CurrentValue); // only September's 1..5
        Assert.Equal(DailyMetricStatus.Partial, card.CurrentStatus);
        Assert.True(card.IsAvailable); // a real partial value is shown, honestly flagged
        Assert.Equal("Data parsial: 1 dari 2 bulan tercatat.", card.CoverageNote);

        Assert.Null(data.Trend[0].VideoViews);  // August: honest null, never 0
        Assert.Equal(DailyMetricStatus.NoData, data.Trend[0].MetricStates[DailyReportMetrics.VideoViews].Status);
        Assert.Equal(15, data.Trend[1].VideoViews);
    }

    // ------------------------------------------------------------------
    // Total Follower: snapshot at the right point in time
    // ------------------------------------------------------------------

    [Fact]
    public async Task TotalFollower_UsesLatestSnapshotInsideThePeriod_NeverAfterIt()
    {
        var snapshots = new[]
        {
            new FollowerSnapshot { AsOfDate = new DateTime(2026, 8, 30), FollowerCount = 1234 },
            new FollowerSnapshot { AsOfDate = new DateTime(2026, 9, 4), FollowerCount = 1300 },
            new FollowerSnapshot { AsOfDate = new DateTime(2026, 9, 30), FollowerCount = 9999 } // after EndDate
        };

        var source = new FakeSource(q => Dataset(
            q, _ => 0, AllAggKeys, Windowed(0, 0, 0), snapshots: snapshots));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var card = Card(data, DailyReportMetrics.TotalFollower);
        Assert.Equal(1300m, card.CurrentValue); // 4 Sep snapshot, never the 30 Sep one
        Assert.Equal(DailyMetricStatus.Complete, card.CurrentStatus);
        Assert.Equal(DailyMetricDeltaKind.PositionOnly, card.DeltaKind);
        Assert.False(card.ShowInComparison); // no delta for a point-in-time position
    }

    [Fact]
    public async Task TotalFollower_NotDeclared_IsUnavailable_EvenWithSnapshots()
    {
        var snapshots = new[] { new FollowerSnapshot { AsOfDate = new DateTime(2026, 9, 4), FollowerCount = 1300 } };
        var keys = new HashSet<string>(StringComparer.Ordinal) { DailyReportMetrics.VideoViews };

        var source = new FakeSource(q => Dataset(q, _ => 0, keys, Windowed(1, 1, 1), snapshots: snapshots));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var card = Card(data, DailyReportMetrics.TotalFollower);
        Assert.Null(card.CurrentValue);
        Assert.Equal(DailyMetricStatus.Unavailable, card.CurrentStatus);
    }

    // ------------------------------------------------------------------
    // Declared-vs-carried honesty, unavailable provider
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnavailableProvider_ReportsUnavailableEverywhere_NoFakeValues_NoPreviousQueries()
    {
        var source = new FakeSource(_ => AccountDailyMetricResult.Unavailable("provider down"));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        Assert.False(data.HasAnyMetricData);
        Assert.All(data.Cards, c =>
        {
            Assert.Null(c.CurrentValue);
            Assert.Null(c.PreviousValue);
            Assert.Equal(DailyMetricStatus.Unavailable, c.CurrentStatus);
        });
        Assert.Contains(data.Notices, n => n.Contains("provider down"));
        Assert.False(data.InteractionComposition.IsAvailable);
        Assert.False(data.ViewerComposition.IsAvailable);
        Assert.Empty(data.InteractionComposition.Segments);

        // Month rows still listed with honest unavailable states.
        Assert.Equal(2, data.Trend.Count);
        Assert.All(data.Trend, r =>
        {
            Assert.False(r.HasAnyValue);
            Assert.Equal(DailyMetricStatus.Unavailable, r.MetricStates[DailyReportMetrics.VideoViews].Status);
        });

        // The provider that cannot answer the FIRST month of the period is not
        // asked again for its remaining months (Daily/Weekly/Monthly convention),
        // and the previous period is never queried when the current one fails.
        // So only 1 of the 2 current month windows is actually requested.
        Assert.Single(source.Queries);
    }

    [Fact]
    public async Task PreviousPeriodUnavailable_KeepsCurrentValues_AndExplainsTheGap()
    {
        var source = new FakeSource(q =>
        {
            // Current months (>= 23 Aug) answer; previous months (before 23 Aug) fail.
            return q.Start >= new DateTime(2026, 8, 23)
                ? Dataset(q, day => day.Day, ViewsAndInteractionsKeys, Windowed(1, 1, 1))
                : AccountDailyMetricResult.Unavailable("previous window down");
        });
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(258m, card.CurrentValue);
        Assert.Null(card.PreviousValue); // honest "-", never the current value or zero
        Assert.Null(card.Difference);
        Assert.Contains(data.Notices, n => n.Contains("Periode pembanding tidak tersedia"));
    }

    // ------------------------------------------------------------------
    // KPI / chart / table reconciliation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cards_Charts_AndTable_RenderTheSameMonthValues()
    {
        var source = new FakeSource(q => Dataset(
            q, day => day.Day * 10, AllAggKeys, Windowed(11, 22, 33),
            followersOn: _ => (5, 2)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(6, data.Cards.Count(c => c.ShowInComparison)); // every card except Total Follower
        Assert.Equal(new[] { "reach", "video_views", "profile_views", "interactions", "engagement_rate", "net_follower" },
            data.ComparisonRows.Select(r => r.Key).ToArray());

        var videoViews = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(data.Trend.Sum(r => r.VideoViews ?? 0), (long?)videoViews.CurrentValue);
        Assert.Equal(data.MonthlyRows.Sum(r => r.VideoViews ?? 0), (long?)videoViews.CurrentValue);

        var netFollower = Card(data, DailyReportMetrics.NetFollower);
        Assert.Equal(data.Trend.Sum(r => r.NetFollower ?? 0), (long?)netFollower.CurrentValue);

        // Ascending for charts, descending for the table - the same month objects.
        Assert.Equal(data.Trend.Reverse(), data.MonthlyRows);
    }

    [Fact]
    public async Task YearRolloverRange_BucketsAcrossYears()
    {
        var source = new FakeSource(q => Dataset(q, day => day.Day, ViewsAndInteractionsKeys, Windowed(1, 1, 1)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(12, 15, 1, 5, startYear: 2026, endYear: 2027));

        Assert.Equal(2, data.CurrentBucketCount);
        Assert.Equal(22, data.CurrentDayCount);
        Assert.Equal(new DateTime(2026, 12, 15), data.Trend[0].Start);
        Assert.Equal(new DateTime(2027, 1, 1), data.Trend[1].Start);
        // 15..31 December = 391 + 1..5 January = 15
        Assert.Equal(406, data.Trend.Sum(r => r.VideoViews ?? 0));
    }

    [Fact]
    public async Task ViewerComposition_OnlyForSingleMonthPeriods_WithVerifiedSplit()
    {
        var viewer = new AccountViewerComposition { NewViewers = 75, ReturningViewers = 25 };
        var source = new FakeSource(q => Dataset(
            q, _ => 0, ViewsAndInteractionsKeys, Windowed(0, 0, 0), viewerComposition: viewer));
        var service = CreateService(source);

        var singleMonth = await service.BuildAsync(Filter(9, 1, 9, 30));
        Assert.True(singleMonth.ViewerComposition.IsAvailable);
        Assert.Equal(100m, singleMonth.ViewerComposition.Total);

        var twoMonths = await service.BuildAsync(Filter(8, 23, 9, 5));
        Assert.False(twoMonths.ViewerComposition.IsAvailable);
        Assert.Contains("satu bulan", twoMonths.ViewerComposition.UnavailableReason);
    }

    // ------------------------------------------------------------------
    // Interactions: availability + aggregation across month buckets
    // (reproduction matrix for the reported "KPI Interactions = -" defect)
    // ------------------------------------------------------------------

    /// <summary>
    /// Windowed totals keyed by the queried bucket start, so each month of a report
    /// carries its OWN verified totals (never one window reused as another month).
    /// The 16 Oct bucket reproduces the reported 8.135 (80,58% / 2,79% / 16,63%).
    /// </summary>
    private static AccountPeriodInteractions BucketTotals(DateTime windowStart)
    {
        if (windowStart == new DateTime(2026, 10, 16)) return Windowed(6555, 227, 1353);
        if (windowStart == new DateTime(2026, 10, 1)) return Windowed(4000, 100, 900);
        if (windowStart == new DateTime(2026, 9, 1)) return Windowed(1000, 400, 600);
        return Windowed(700, 70, 70);
    }

    [Fact]
    public async Task Interactions_SingleMonthWindowed_MatchesReportedKpiTableAndDonut()
    {
        // Scenario A: 16-29 Oktober 2026 - one month bucket, complete data.
        var source = new FakeSource(q => Dataset(q, _ => 1000, ViewsAndInteractionsKeys, BucketTotals(q.Start)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(10, 16, 10, 29));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(8135m, interactions.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);
        Assert.True(interactions.IsAvailable);

        var composition = data.InteractionComposition;
        Assert.True(composition.IsAvailable);
        Assert.Equal(8135m, composition.Total);
        Assert.Collection(
            composition.Segments,
            s => { Assert.Equal("like", s.Key); Assert.Equal(6555m, s.Value); Assert.Equal(80.58m, Math.Round(s.Percentage!.Value, 2)); },
            s => { Assert.Equal("comment", s.Key); Assert.Equal(227m, s.Value); Assert.Equal(2.79m, Math.Round(s.Percentage!.Value, 2)); },
            s => { Assert.Equal("share", s.Key); Assert.Equal(1353m, s.Value); Assert.Equal(16.63m, Math.Round(s.Percentage!.Value, 2)); });

        // KPI == monthly table == donut (same number on every surface).
        Assert.Single(data.MonthlyRows);
        Assert.Equal(8135m, data.MonthlyRows.Sum(r => r.Interactions ?? 0));
        Assert.Equal(composition.Total, composition.Segments.Sum(s => s.Value));
        Assert.Equal(interactions.CurrentValue, data.Trend.Sum(r => r.Interactions ?? 0));
    }

    [Fact]
    public async Task Interactions_TwoMonths_SumsBucketTotals_AndComposesFromTheAggregates()
    {
        // Scenario D: 1 September - 29 Oktober 2026, both months complete.
        var source = new FakeSource(q => Dataset(q, _ => 1000, ViewsAndInteractionsKeys, BucketTotals(q.Start)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(9, 1, 10, 29));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(7000m, interactions.CurrentValue); // 2000 (Sep) + 5000 (Okt)
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);

        Assert.Equal(2, data.MonthlyRows.Count);
        Assert.Equal(2000L, data.MonthlyRows.Single(r => r.Start.Month == 9).Interactions);
        Assert.Equal(5000L, data.MonthlyRows.Single(r => r.Start.Month == 10).Interactions);
        Assert.Equal(7000m, data.MonthlyRows.Sum(r => r.Interactions ?? 0));

        var composition = data.InteractionComposition;
        Assert.True(composition.IsAvailable);
        Assert.Equal(7000m, composition.Total);
        // AGGREGATE totals: 5000/7000 = 71,4286% - NOT the average of the two
        // monthly like rates ((50% + 80%) / 2 = 65%).
        Assert.Equal(71.4286m, composition.Segments.Single(s => s.Key == "like").Percentage);
        Assert.Equal(7.1429m, composition.Segments.Single(s => s.Key == "comment").Percentage);
        Assert.Equal(21.4286m, composition.Segments.Single(s => s.Key == "share").Percentage);
        Assert.Equal(7000m, composition.Segments.Sum(s => s.Value));
    }

    [Fact]
    public async Task Interactions_SecondMonthUnavailable_KeepsTheValidMonthAsPartial()
    {
        // September answers; October's provider call fails (rate limit / transport).
        var source = new FakeSource(q => q.Start >= new DateTime(2026, 10, 1)
            ? AccountDailyMetricResult.Unavailable("list API rate limited (429)")
            : Dataset(q, _ => 1000, ViewsAndInteractionsKeys, BucketTotals(q.Start)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(9, 1, 10, 29));

        // The valid month is NOT discarded - it surfaces honestly as Partial.
        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(2000m, interactions.CurrentValue);
        Assert.Equal(DailyMetricStatus.Partial, interactions.CurrentStatus);
        Assert.True(interactions.IsAvailable);
        Assert.Equal("Data parsial: 1 dari 2 bulan tercatat.", interactions.CoverageNote);

        // Video Views keeps the same honest partial coverage.
        var videoViews = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(30000m, videoViews.CurrentValue); // 30 days x 1000
        Assert.Equal(DailyMetricStatus.Partial, videoViews.CurrentStatus);

        // Composition is withheld (coverage is not complete) - never a half period.
        Assert.False(data.InteractionComposition.IsAvailable);
        Assert.Contains("tidak lengkap", data.InteractionComposition.UnavailableReason);
    }

    [Fact]
    public async Task Interactions_SecondMonthWithoutData_KeepsTheValidMonthAsPartial()
    {
        // September answers with an EMPTY dataset (no records, no windowed totals).
        var source = new FakeSource(q => Dataset(
            q, _ => 1000, ViewsAndInteractionsKeys,
            windowedInteractions: q.Start >= new DateTime(2026, 10, 1) ? BucketTotals(q.Start) : null,
            includeRecords: q.Start >= new DateTime(2026, 10, 1)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(9, 1, 10, 29));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(5000m, interactions.CurrentValue); // only October
        Assert.Equal(DailyMetricStatus.Partial, interactions.CurrentStatus);
        Assert.True(interactions.IsAvailable);

        Assert.Null(data.Trend.Single(r => r.Start.Month == 9).Interactions); // honest null, never 0
        Assert.Equal(5000L, data.Trend.Single(r => r.Start.Month == 10).Interactions);
        Assert.False(data.InteractionComposition.IsAvailable);
    }

    [Fact]
    public async Task Interactions_ComponentMissingEverywhere_KpiAndDonutStayUnavailable()
    {
        // Comments exist neither in the windowed total nor on any day: the period
        // total is null (no partial sum) and the donut must not invent a share.
        var source = new FakeSource(q => Dataset(
            q, _ => 1000, ViewsAndInteractionsKeys,
            windowedInteractions: null,
            interactionsOn: _ => (10, null, 5)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(10, 16, 10, 29));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Null(interactions.CurrentValue);
        Assert.False(interactions.IsAvailable);
        Assert.NotEqual(DailyMetricStatus.Complete, interactions.CurrentStatus);

        Assert.False(data.InteractionComposition.IsAvailable);
        Assert.Empty(data.InteractionComposition.Segments);
    }

    [Fact]
    public async Task Interactions_TotalZero_IsARealZero_KpiShown_DonutEmpty()
    {
        var source = new FakeSource(q => Dataset(q, _ => 1000, ViewsAndInteractionsKeys, Windowed(0, 0, 0)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(10, 16, 10, 29));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(0m, interactions.CurrentValue);
        Assert.True(interactions.IsAvailable); // 0 is data - null is not
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);

        // 0/0 shares cannot be rendered as a composition.
        Assert.False(data.InteractionComposition.IsAvailable);
    }

    [Fact]
    public async Task Interactions_WindowedTotalsAreAuthoritative_DailyRecordsNeverAddedOnTop()
    {
        // Both month buckets carry a windowed total AND daily values: the period
        // must be 2 x 100 = 200, never 200 + the 177 daily interactions.
        var source = new FakeSource(q => Dataset(
            q, _ => 1000, ViewsAndInteractionsKeys,
            windowedInteractions: Windowed(60, 30, 10),
            interactionsOn: _ => (1, 1, 1)));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(9, 1, 10, 29));

        Assert.Equal(200m, Card(data, DailyReportMetrics.Interactions).CurrentValue);
        Assert.Equal(200m, data.InteractionComposition.Total);
        Assert.Equal(200m, data.MonthlyRows.Sum(r => r.Interactions ?? 0));
    }

    [Fact]
    public async Task ScenarioB_CurrentOctoberInteractionsAvailable_PreviousSeptemberUnavailable_KeepsCurrentCard()
    {
        // Scenario B (1..29 Okt 2026, previous 2..30 Sep 2026): the CURRENT window
        // answers with verified windowed totals while the equal-length previous
        // window fails (batch lookback rejection 28001022). The failed September
        // query must never erase the valid October value.
        var source = new FakeSource(q => q.Start >= new DateTime(2026, 10, 1)
            ? Dataset(q, _ => 1000, ViewsAndInteractionsKeys, Windowed(4000, 100, 900))
            : AccountDailyMetricResult.Unavailable("batch API menolak jendela di luar lookback (code 28001022)"));
        var service = CreateService(source);

        var data = await service.BuildAsync(Filter(10, 1, 10, 29));

        var interactions = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(5000m, interactions.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, interactions.CurrentStatus);
        Assert.True(interactions.IsAvailable);

        // Previous / difference stay honest "-" without touching the current value.
        Assert.Null(interactions.PreviousValue);
        Assert.Null(interactions.Difference);
        Assert.Null(interactions.GrowthPercentage);

        Assert.NotNull(Card(data, DailyReportMetrics.VideoViews).CurrentValue);
        Assert.True(data.InteractionComposition.IsAvailable);
        Assert.Equal(5000m, data.InteractionComposition.Total);
        Assert.Contains(data.Notices, n => n.Contains("Periode pembanding tidak tersedia"));

        // Current period was asked (1 month bucket); the previous one was attempted
        // and answered unavailable instead of being silently skipped.
        Assert.Contains(source.Queries, q => q.Start == new DateTime(2026, 10, 1));
        Assert.Contains(source.Queries, q => q.Start == new DateTime(2026, 9, 2));
    }
}
