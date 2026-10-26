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
/// Service-level behaviour of the Weekly Report: Monday-Sunday bucket queries
/// (sequential, window-bounded), additive aggregation with honest partial /
/// unavailable states, windowed-interaction totals (never distributed, never
/// double counted), period-comparison parity with the Daily Report, and
/// card / chart / table reconciliation from one set of bucket results.
/// Uses a scripted fake provider - no DB, no network, no fabricated production data.
/// </summary>
public class WeeklyPerformanceServiceTests
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
        private int _active;

        public FakeSource(Func<AccountDailyMetricQuery, AccountDailyMetricResult> handler) => _handler = handler;

        public List<AccountDailyMetricQuery> Queries { get; } = new();
        public int MaxConcurrency { get; private set; }

        public Task<AccountDailyMetricResult> GetAsync(AccountDailyMetricQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);

            // Concurrency probe: the service must read buckets sequentially.
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

    private static readonly IReadOnlySet<string> DailyOnlyKeys =
        new HashSet<string>(StringComparer.Ordinal) { DailyReportMetrics.VideoViews };

    private static readonly IReadOnlySet<string> ViewsAndInteractionsKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            DailyReportMetrics.VideoViews,
            DailyReportMetrics.Interactions
        };

    private static WeeklyReportFilter Filter(int startMonth, int startDay, int endMonth, int endDay, int year = 2026)
        => new() { StartDate = new DateTime(year, startMonth, startDay), EndDate = new DateTime(year, endMonth, endDay) };

    private static WeeklyPerformanceService CreateService(IAccountDailyMetricSource source)
        => new(source, new FakeShopTimeZone());

    private static DailyReportMetric Card(WeeklyReportData data, string key)
        => data.Cards.Single(c => c.Key == key);

    /// <summary>
    /// Every calendar day of the half-open window gets a record with deterministic
    /// Video Views; optional daily interaction components per day.
    /// </summary>
    private static AccountDailyMetricResult ViewsDataset(
        AccountDailyMetricQuery query,
        Func<DateTime, long> viewsOn,
        IReadOnlySet<string>? availableKeys,
        AccountPeriodInteractions? windowedInteractions = null,
        Func<DateTime, (long? Likes, long? Comments, long? Shares)>? dailyInteractions = null)
    {
        var records = new List<AccountDailyMetricRecord>();
        for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
        {
            var (likes, comments, shares) = dailyInteractions?.Invoke(day)
                ?? (null, null, null);

            records.Add(new AccountDailyMetricRecord
            {
                Date = day,
                VideoViews = viewsOn(day),
                Likes = likes,
                Comments = comments,
                Shares = shares
            });
        }

        return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = records,
            SourceName = "fake-overview",
            AvailableMetricKeys = availableKeys,
            PeriodInteractions = windowedInteractions
        });
    }

    // ------------------------------------------------------------------
    // Query behaviour
    // ------------------------------------------------------------------

    [Fact]
    public async Task Build_QueriesOneWindowPerBucket_InCurrentAndPreviousPeriod_Sequentially()
    {
        var source = new FakeSource(query => ViewsDataset(query, day => day.DayOfYear, DailyOnlyKeys));

        await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        // 3 current buckets + 3 previous buckets = 6 windows, sequential (no fan-out).
        Assert.Equal(6, source.Queries.Count);
        Assert.Equal(1, source.MaxConcurrency);

        var currentWindows = source.Queries.Take(3).ToList();
        var previousWindows = source.Queries.Skip(3).ToList();

        // Current: 23/08 (1d), 24-30/08 (7d), 31/08-05/09 (6d) - half-open, clipped.
        Assert.Equal((new DateTime(2026, 8, 23), new DateTime(2026, 8, 24)), (currentWindows[0].Start, currentWindows[0].EndExclusive));
        Assert.Equal((new DateTime(2026, 8, 24), new DateTime(2026, 8, 31)), (currentWindows[1].Start, currentWindows[1].EndExclusive));
        Assert.Equal((new DateTime(2026, 8, 31), new DateTime(2026, 9, 6)), (currentWindows[2].Start, currentWindows[2].EndExclusive));

        // Previous (09/08 is a Sunday): 09/08 (1d), 10-16/08 (7d), 17-22/08 (6d) -
        // equal duration (14 days), no overlap.
        Assert.Equal((new DateTime(2026, 8, 9), new DateTime(2026, 8, 10)), (previousWindows[0].Start, previousWindows[0].EndExclusive));
        Assert.Equal((new DateTime(2026, 8, 10), new DateTime(2026, 8, 17)), (previousWindows[1].Start, previousWindows[1].EndExclusive));
        Assert.Equal((new DateTime(2026, 8, 17), new DateTime(2026, 8, 23)), (previousWindows[2].Start, previousWindows[2].EndExclusive));

        // No window exceeds 7 days and none spans the whole 14-day period.
        Assert.All(source.Queries, q =>
        {
            var days = (q.EndExclusive - q.Start).Days;
            Assert.InRange(days, 1, WeeklyBucket.FullWeekDays);
        });
    }

    [Fact]
    public async Task Build_UnavailableSource_AsksProviderOnce_AndStaysHonest()
    {
        var source = new FakeSource(_ => AccountDailyMetricResult.Unavailable("transport failure"));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        // A provider that cannot answer the first window is not asked again.
        Assert.Single(source.Queries);

        Assert.False(data.HasAnyMetricData);
        Assert.All(data.Cards, card =>
        {
            Assert.Null(card.CurrentValue);
            Assert.Equal(DailyMetricStatus.Unavailable, card.CurrentStatus);
            Assert.Contains("transport failure", card.UnavailableReason);
        });

        Assert.All(data.WeeklyRows, row => Assert.False(row.HasAnyValue));
        Assert.Contains(data.Notices, n => n.Contains("transport failure"));
    }

    [Fact]
    public async Task Build_ReversedRange_Throws_NeverSwaps()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 1, DailyOnlyKeys));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService(source).BuildAsync(Filter(9, 5, 8, 23)));

        Assert.Empty(source.Queries);
    }

    [Fact]
    public async Task Build_SingleDayRange_OneBucketOneQuery_PartialWeek()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 42, DailyOnlyKeys));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 8, 23));

        Assert.Equal(2, source.Queries.Count); // 1 current + 1 previous bucket.
        Assert.Equal(1, data.CurrentBucketCount);
        Assert.Equal(1, data.PreviousBucketCount);

        var row = Assert.Single(data.WeeklyRows);
        Assert.Equal(1, row.DayCount);
        Assert.True(row.IsPartial);
        Assert.Equal(42, row.VideoViews);
    }

    [Fact]
    public async Task Build_PeriodInfo_MatchesEqualDurationConvention()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 1, DailyOnlyKeys));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(new DateTime(2026, 8, 23), data.CurrentStart);
        Assert.Equal(new DateTime(2026, 9, 5), data.CurrentEnd);
        Assert.Equal(new DateTime(2026, 8, 9), data.PreviousStart);
        Assert.Equal(new DateTime(2026, 8, 22), data.PreviousEnd);
        Assert.Equal(14, data.CurrentDayCount);
        Assert.Equal(14, data.PreviousDayCount);
        Assert.Equal(3, data.CurrentBucketCount);
        Assert.Equal(3, data.PreviousBucketCount);
    }

    // ------------------------------------------------------------------
    // Aggregation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Build_VideoViews_SumsEveryDay_AndReconcilesCardRowsChart()
    {
        var source = new FakeSource(query => ViewsDataset(query, day => day.DayOfYear * 10L, DailyOnlyKeys));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        var expected = 0L;
        for (var day = new DateTime(2026, 8, 23); day <= new DateTime(2026, 9, 5); day = day.AddDays(1))
        {
            expected += day.DayOfYear * 10L;
        }

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal((decimal)expected, card.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, card.CurrentStatus);
        Assert.Null(card.UnavailableReason);

        // Table, chart and period total read the SAME bucket results.
        Assert.Equal(expected, data.WeeklyRows.Sum(r => r.VideoViews!.Value));
        Assert.Equal(
            data.WeeklyRows.AsEnumerable().Reverse().Select(r => r.Start).ToList(),
            data.Trend.Select(r => r.Start).ToList());
        Assert.All(data.Trend, row => Assert.Equal(DailyMetricStatus.Complete, row.MetricStates[DailyReportMetrics.VideoViews].Status));
    }

    [Fact]
    public async Task Build_PartialBucket_CoverageHonest_AndNeverZeroFilled()
    {
        // Exactly one full week (24-30/08), but only 4 of its 7 days carry records.
        var source = new FakeSource(query => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = Enumerable.Range(0, 7)
                .Where(i => i < 4) // 24, 25, 26, 27 August only
                .Select(i => new AccountDailyMetricRecord
                {
                    Date = query.Start.AddDays(i),
                    VideoViews = 10
                })
                .ToList(),
            SourceName = "fake-overview",
            AvailableMetricKeys = DailyOnlyKeys
        }));

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(40m, card.CurrentValue); // 4 days x 10 - the missing days are NOT zeroes.
        Assert.Equal(DailyMetricStatus.Partial, card.CurrentStatus);
        Assert.NotNull(card.CoverageNote);
        Assert.Contains("sebagian hari", card.CoverageNote);

        var row = Assert.Single(data.WeeklyRows);
        var state = row.MetricStates[DailyReportMetrics.VideoViews];
        Assert.Equal(DailyMetricStatus.Partial, state.Status);
        Assert.Equal(4, state.DaysCovered);
        Assert.Equal(7, state.DaysInBucket);
    }

    [Fact]
    public async Task Build_WindowedInteractions_RepresentExactlyTheirBucket_NeverDistributed()
    {
        // Only the 7-day bucket's window carries an authoritative windowed total;
        // the other buckets have records without any interaction values.
        var source = new FakeSource(query =>
        {
            var isFullWeek = query.Start == new DateTime(2026, 8, 24) && query.EndExclusive == new DateTime(2026, 8, 31);
            return ViewsDataset(query, _ => 100, ViewsAndInteractionsKeys,
                windowedInteractions: isFullWeek
                    ? new AccountPeriodInteractions { Likes = 100, Comments = 20, Shares = 30 }
                    : null);
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(3, data.WeeklyRows.Count);

        // The bucket with the windowed total shows it, Complete.
        var fullWeekRow = data.WeeklyRows.Single(r => r.DayCount == 7);
        Assert.Equal(150, fullWeekRow.Interactions);
        Assert.Equal(DailyMetricStatus.Complete, fullWeekRow.MetricStates[DailyReportMetrics.Interactions].Status);

        // The OTHER weeks stay null - the total is never spread across weeks.
        Assert.All(data.WeeklyRows.Where(r => r.DayCount != 7), row => Assert.Null(row.Interactions));

        var card = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(150m, card.CurrentValue);
        Assert.Equal(DailyMetricStatus.Partial, card.CurrentStatus); // 1 of 3 weeks covered
        Assert.Contains("1 dari 3 minggu tercatat", card.CoverageNote);
    }

    [Fact]
    public async Task Build_WindowedInteractionsWinOverDailyValues_NeverDoubleCounted()
    {
        var source = new FakeSource(query =>
        {
            // Daily component values (2 likes/day) coexist with a windowed total of
            // 100 likes for the same window - the windowed total wins, exactly once.
            var isFullWeek = query.Start == new DateTime(2026, 8, 24);
            return ViewsDataset(query, _ => 100, ViewsAndInteractionsKeys,
                windowedInteractions: isFullWeek
                    ? new AccountPeriodInteractions { Likes = 100, Comments = 20, Shares = 30 }
                    : null,
                dailyInteractions: isFullWeek
                    ? _ => (2L, 1L, 1L)
                    : null);
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        var row = Assert.Single(data.WeeklyRows);
        Assert.Equal(150, row.Interactions); // 100+20+30, NOT 150 + the daily sums (14+7+7).

        var card = Card(data, DailyReportMetrics.Interactions);
        Assert.Equal(150m, card.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, card.CurrentStatus);
    }

    [Fact]
    public async Task Build_DailyInteractionsSum_UsedWhenNoWindowedTotal_FlagsMissingDays()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 100, ViewsAndInteractionsKeys,
            dailyInteractions: day => day.Date <= new DateTime(2026, 8, 26)
                ? (5L, 1L, 1L)
                : ((long?)null, (long?)null, (long?)null)));

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        var row = Assert.Single(data.WeeklyRows);
        Assert.Equal(21, row.Interactions); // 3 covered days x (5+1+1)
        var state = row.MetricStates[DailyReportMetrics.Interactions];
        Assert.Equal(DailyMetricStatus.Partial, state.Status);
        Assert.Equal(3, state.DaysCovered);

        // Composition must NOT render from incomplete inputs.
        Assert.False(data.InteractionComposition.IsAvailable);
        Assert.Contains("tidak lengkap", data.InteractionComposition.UnavailableReason);
    }

    [Fact]
    public async Task Build_InteractionsComposition_RendersOnlyWithCompleteCoverage()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 100, ViewsAndInteractionsKeys,
            windowedInteractions: new AccountPeriodInteractions { Likes = 81, Comments = 8, Shares = 11 }));

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        Assert.True(data.InteractionComposition.IsAvailable);
        Assert.Equal(100m, data.InteractionComposition.Total);
        var segments = data.InteractionComposition.Segments;
        Assert.Equal(new[] { "like", "comment", "share" }, segments.Select(s => s.Key).ToList());
        Assert.Equal(new decimal?[] { 81, 8, 11 }, segments.Select(s => s.Value).ToList());
        Assert.Equal(100m, segments.Sum(s => s.Percentage!.Value));
    }

    [Fact]
    public async Task Build_UnavailableMetrics_NeverZero_ReasonsHonest()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 100, DailyOnlyKeys));

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        foreach (var key in new[]
                 {
                     DailyReportMetrics.Reach,
                     DailyReportMetrics.ProfileViews,
                     DailyReportMetrics.NetFollower,
                     DailyReportMetrics.TotalFollower
                 })
        {
            var card = Card(data, key);
            Assert.Null(card.CurrentValue);
            Assert.Equal(DailyMetricStatus.Unavailable, card.CurrentStatus);
            Assert.False(string.IsNullOrWhiteSpace(card.UnavailableReason));
        }

        // Total Follower is PositionOnly and excluded from the comparison table.
        var totalFollower = Card(data, DailyReportMetrics.TotalFollower);
        Assert.Equal(DailyMetricDeltaKind.PositionOnly, totalFollower.DeltaKind);
        Assert.False(totalFollower.ShowInComparison);
        Assert.Equal(6, data.ComparisonRows.Count);
    }

    [Fact]
    public async Task Build_EngagementRate_UnavailableWhileReachIsUnavailable_NeverViewsBased()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 100, ViewsAndInteractionsKeys,
            windowedInteractions: new AccountPeriodInteractions { Likes = 50, Comments = 10, Shares = 40 }));

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        // Interactions are available (100) but Reach is not - ER stays unavailable.
        Assert.Equal(100m, Card(data, DailyReportMetrics.Interactions).CurrentValue);

        var er = Card(data, DailyReportMetrics.EngagementRate);
        Assert.Null(er.CurrentValue);
        Assert.Equal(DailyMetricStatus.Unavailable, er.CurrentStatus);
        Assert.Contains("Reach", er.UnavailableReason);
        Assert.Equal(DailyMetricDeltaKind.PercentagePoints, er.DeltaKind);

        Assert.All(data.WeeklyRows, row => Assert.Null(row.EngagementRate));
    }

    [Fact]
    public async Task Build_EngagementRate_ComputedFromPeriodTotals_WhenReachDeclared()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 1000, availableKeys: null,
            windowedInteractions: new AccountPeriodInteractions { Likes = 100, Comments = 20, Shares = 30 }));

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        // 100 interactions / 7000 reach x 100 = 1.4286%
        var er = Card(data, DailyReportMetrics.EngagementRate);
        Assert.Equal(1.4286m, er.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, er.CurrentStatus);
        Assert.Equal(DailyMetricDeltaKind.PercentagePoints, er.DeltaKind);
        Assert.Null(er.GrowthPercentage); // percentage-point metrics never show growth %

        var row = Assert.Single(data.WeeklyRows);
        Assert.Equal(1.4286m, row.EngagementRate); // 100 / 7000 x 100
    }

    [Fact]
    public async Task Build_GenuineZero_IsAValue_NotMissingData()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 0, DailyOnlyKeys));

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(0m, card.CurrentValue);
        Assert.True(card.IsAvailable);
        Assert.Equal(DailyMetricStatus.Complete, card.CurrentStatus);

        // A valid zero is data - the "no data recorded" notice must not fire.
        Assert.DoesNotContain(data.Notices, n => n.Contains("Tidak ada data tercatat"));
    }

    [Fact]
    public async Task Build_ZeroBaselineDelta_DifferenceShown_GrowthNull()
    {
        var source = new FakeSource(query =>
        {
            // The single previous window (23/08) returns zeroes; the current one values.
            var isPrevious = query.EndExclusive <= new DateTime(2026, 8, 24);
            return ViewsDataset(query, _ => isPrevious ? 0 : 500, DailyOnlyKeys);
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(3500m, card.CurrentValue);
        Assert.Equal(0m, card.PreviousValue);
        Assert.Equal(3500m, card.Difference);
        Assert.Null(card.GrowthPercentage); // growth against a zero baseline is undefined, never Infinity
    }

    [Fact]
    public async Task Build_Delta_UsesAggregatedPeriodTotals_WeeklyTotalsNotWeekPairing()
    {
        var source = new FakeSource(query => ViewsDataset(query,
            // Previous windows (all end on/before 23/08) get 40/day, current ones 100/day.
            _ => query.EndExclusive <= new DateTime(2026, 8, 23) ? 40 : 100,
            DailyOnlyKeys));

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(1400m, card.CurrentValue);  // 14 days x 100
        Assert.Equal(560m, card.PreviousValue);  // 14 days x 40
        Assert.Equal(840m, card.Difference);
        Assert.Equal(150m, card.GrowthPercentage);
    }

    [Fact]
    public async Task Build_MiddleWindowFailure_ContinuesOthers_HonestPartialPeriod()
    {
        var source = new FakeSource(query =>
        {
            // The SECOND current bucket (24-30/08) fails; the rest answer.
            var failing = query.Start == new DateTime(2026, 8, 24) && query.EndExclusive == new DateTime(2026, 8, 31);
            return failing
                ? AccountDailyMetricResult.Unavailable("window timeout")
                : ViewsDataset(query, _ => 10, DailyOnlyKeys);
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 23, 9, 5));

        Assert.Equal(6, source.Queries.Count); // 3 current (failure does not stop the rest) + 3 previous.

        var failedRow = data.WeeklyRows.Single(r => r.Start == new DateTime(2026, 8, 24));
        Assert.False(failedRow.HasAnyValue);
        Assert.Equal(DailyMetricStatus.Unavailable, failedRow.MetricStates[DailyReportMetrics.VideoViews].Status);
        Assert.Contains("window timeout", failedRow.MetricStates[DailyReportMetrics.VideoViews].Reason);

        // Other buckets keep their real values; the period total only sums them.
        var card = Card(data, DailyReportMetrics.VideoViews);
        Assert.Equal(70m, card.CurrentValue); // 7 real days - the failed week is not zero.
        Assert.Equal(DailyMetricStatus.Partial, card.CurrentStatus);
        Assert.True(data.HasAnyMetricData);
        Assert.Contains(data.Notices, n => n.Contains("Data parsial"));
    }

    [Fact]
    public async Task Build_NoDataPeriod_HonestNotice_NoFakeValues()
    {
        var source = new FakeSource(query => AccountDailyMetricResult.Available(new AccountDailyMetricDataset
        {
            Records = Array.Empty<AccountDailyMetricRecord>(),
            SourceName = "fake-overview",
            AvailableMetricKeys = DailyOnlyKeys
        }));

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        Assert.False(data.HasAnyMetricData);
        Assert.Equal(DailyMetricStatus.NoData, Card(data, DailyReportMetrics.VideoViews).CurrentStatus);
        Assert.Null(Card(data, DailyReportMetrics.VideoViews).CurrentValue);
        Assert.Contains(data.Notices, n => n.Contains("Tidak ada data tercatat"));
    }

    [Fact]
    public async Task Build_SourceNotes_ListEveryCardMetric_WithHonestStatus()
    {
        var source = new FakeSource(query => ViewsDataset(query, _ => 10, DailyOnlyKeys));

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        Assert.Contains(data.SourceNotes, n => n.Metric == "Video Views" && n.Status.Contains("fake-overview"));
        Assert.Contains(data.SourceNotes, n => n.Metric == "Reach" && n.Status.Contains("belum tersedia"));
        Assert.Contains(data.SourceNotes, n => n.Metric == "Engagement Rate");
        Assert.Contains(data.SourceNotes, n => n.Metric == "Net Follower");
        Assert.Contains(data.SourceNotes, n => n.Metric == "Total Follower");
        Assert.Contains(data.SourceNotes, n => n.Metric == "Interactions");
        Assert.Contains(data.SourceNotes, n => n.Metric == "Profile Views");
    }

    [Fact]
    public async Task Build_ViewerComposition_OnlyForSingleWindowPeriods()
    {
        AccountDailyMetricResult WithViewers(AccountDailyMetricQuery query)
        {
            var result = ViewsDataset(query, _ => 10, DailyOnlyKeys);
            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = result.Dataset!.Records,
                SourceName = result.Dataset.SourceName,
                AvailableMetricKeys = result.Dataset.AvailableMetricKeys,
                ViewerComposition = new AccountViewerComposition { NewViewers = 52, ReturningViewers = 48 }
            });
        }

        // Single-day range = one queried window covering the whole period: allowed.
        var single = await CreateService(new FakeSource(WithViewers)).BuildAsync(Filter(8, 24, 8, 24));
        Assert.True(single.ViewerComposition.IsAvailable);
        Assert.Equal(100m, single.ViewerComposition.Total);

        // Multi-bucket period: unique viewers are NEVER summed across windows.
        var multi = await CreateService(new FakeSource(WithViewers)).BuildAsync(Filter(8, 23, 9, 5));
        Assert.False(multi.ViewerComposition.IsAvailable);
        Assert.Contains("double counting", multi.ViewerComposition.UnavailableReason);
    }

    [Fact]
    public async Task Build_FollowerSnapshot_UsedInsidePeriod_WhenSupplied()
    {
        var source = new FakeSource(query =>
        {
            var base2 = ViewsDataset(query, _ => 10, availableKeys: null);
            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = base2.Dataset!.Records,
                SourceName = base2.Dataset.SourceName,
                FollowerSnapshots = new List<FollowerSnapshot>
                {
                    new() { AsOfDate = new DateTime(2026, 8, 28), FollowerCount = 20177 }
                }
            });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        var card = Card(data, DailyReportMetrics.TotalFollower);
        Assert.Equal(20177m, card.CurrentValue);
        Assert.Equal(DailyMetricStatus.Complete, card.CurrentStatus);
    }

    [Fact]
    public async Task Build_SnapshotAfterEnd_NeverUsed()
    {
        var source = new FakeSource(query =>
        {
            var base2 = ViewsDataset(query, _ => 10, availableKeys: null);
            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = base2.Dataset!.Records,
                SourceName = base2.Dataset.SourceName,
                FollowerSnapshots = new List<FollowerSnapshot>
                {
                    new() { AsOfDate = new DateTime(2026, 9, 1), FollowerCount = 999 } // after the 30/08 end
                }
            });
        });

        var data = await CreateService(source).BuildAsync(Filter(8, 24, 8, 30));

        var card = Card(data, DailyReportMetrics.TotalFollower);
        Assert.Null(card.CurrentValue);
        Assert.Equal(DailyMetricStatus.NoData, card.CurrentStatus);
    }
}
