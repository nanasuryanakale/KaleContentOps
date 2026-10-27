using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using KaleContentOps.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using KaleContentOps.Services.CreativePerformance;

namespace KaleContentOps.Services.CreativePerformance;

// Creative Performance - Monthly Report service.
// Design: equal-duration Previous Period, month-bucket aggregation from daily records,
// sequential provider queries, explicit availability/status, honest unavailable reasons.

public sealed class MonthlyPerformanceService : IMonthlyPerformanceService
{
    private const string NoDataPeriodReason = "Belum ada data tercatat pada periode ini.";
    private const string NoDataMonthReason = "Belum ada data tercatat pada bulan ini.";
    private const string ERRequiresReachReason =
        "Engagement Rate membutuhkan Reach (formula Interactions / Reach x 100%) dan Reach belum memiliki " +
        "sumber data terverifikasi - nilai tetap ditampilkan \"-\" (bukan diganti dengan Views).";

    private static readonly (string Key, string Label)[] CardMetrics =
    {
        (DailyReportMetrics.Reach, "Reach"), (DailyReportMetrics.VideoViews, "Video Views"),
        (DailyReportMetrics.ProfileViews, "Profile Views"), (DailyReportMetrics.Interactions, "Interactions"),
        (DailyReportMetrics.EngagementRate, "Engagement Rate"), (DailyReportMetrics.NetFollower, "Net Follower"),
        (DailyReportMetrics.TotalFollower, "Total Follower")
    };

    private readonly IAccountDailyMetricSource _source;
    private readonly IShopTimeZone _shopTimeZone;
    private readonly ILogger<MonthlyPerformanceService> _logger;

    public MonthlyPerformanceService(
        IAccountDailyMetricSource source,
        IShopTimeZone shopTimeZone,
        ILogger<MonthlyPerformanceService>? logger = null)
    {
        _source = source;
        _shopTimeZone = shopTimeZone;
        _logger = logger ?? NullLogger<MonthlyPerformanceService>.Instance;
    }

    public async Task<MonthlyReportData> BuildAsync(MonthlyReportFilter filter, CancellationToken cancellationToken = default)
    {
        var current = MonthlyReportDateRange.Create(filter.StartDate, filter.EndDate);
        var previous = current.Previous();

        var currentMonths = current.Months().ToList();
        var previousMonths = previous.Months().ToList();

        var currentPeriod = await BuildPeriodAsync(currentMonths, cancellationToken).ConfigureAwait(false);

        // No point asking the provider twice when it cannot answer at all
        var previousPeriod = currentPeriod.SourceAvailable
            ? await BuildPeriodAsync(previousMonths, cancellationToken).ConfigureAwait(false)
            : BuildPreviousPeriodContext(previousMonths, currentPeriod);

        var cards = BuildCards(currentPeriod, previousPeriod, currentPeriod.LimitedSourceReason);
        var trendRows = BuildTrendRows(currentPeriod);
        var monthlyRows = trendRows.AsEnumerable().Reverse().ToList();

        var interactionComposition = BuildInteractionComposition(currentPeriod);
        var viewerComposition = BuildViewerComposition(currentPeriod);

        var notices = BuildNotices(currentPeriod, previousPeriod, cards);

        _logger.LogInformation(
            "Monthly Report built {Start}..{End} into {BucketCount} months (previous {PreviousStart}..{PreviousEnd}, {PreviousBucketCount} months): sourceAvailable={SourceAvailable}",
            current.Start.ToString("yyyy-MM-dd"),
            current.End.ToString("yyyy-MM-dd"),
            currentMonths.Count,
            previous.Start.ToString("yyyy-MM-dd"),
            previous.End.ToString("yyyy-MM-dd"),
            previousMonths.Count,
            currentPeriod.SourceAvailable);

        var engagementRateValue = cards.Single(c => c.Key == DailyReportMetrics.EngagementRate).CurrentValue;
        var totalFollowerValue = cards.Single(c => c.Key == DailyReportMetrics.TotalFollower).CurrentValue;
        return new MonthlyReportData
        {
            CurrentStart = current.Start,
            CurrentEnd = current.End,
            PreviousStart = previous.Start,
            PreviousEnd = previous.End,
            CurrentDayCount = current.DayCount,
            PreviousDayCount = previous.DayCount,
            CurrentBucketCount = currentMonths.Count,
            PreviousBucketCount = previousMonths.Count,
            TimeZoneId = _shopTimeZone.TimeZoneId,
            TimeZoneLabel = _shopTimeZone.TimeZoneId == ShopTimeZoneOptions.DefaultTimeZoneId
                ? "GMT+7"
                : _shopTimeZone.TimeZoneId,
            Cards = cards,
            ComparisonRows = cards.Where(c => c.ShowInComparison).ToList(),
            Trend = trendRows,
            MonthlyRows = monthlyRows,
            InteractionComposition = interactionComposition,
            ViewerComposition = viewerComposition,
            Notices = notices,
            SourceNotes = BuildSourceNotes(currentPeriod),
            HasAnyMetricData = cards.Any(c => c.CurrentValue.HasValue),
            EngagementRate = (decimal?)(cards.Single(c => c.Key == DailyReportMetrics.EngagementRate).CurrentValue),
            TotalFollower = (long?)(cards.Single(c => c.Key == DailyReportMetrics.TotalFollower).CurrentValue)
        };
    }

    private async Task<PeriodContext> BuildPeriodAsync(List<MonthlyBucket> months, CancellationToken cancellationToken)
    {
        var monthContexts = new List<MonthContext>(months.Count);

        for (var i = 0; i < months.Count; i++)
        {
            var month = months[i];

            // A provider that cannot answer the FIRST month of the period is not
            // asked again for its remaining months (Daily/Weekly/Monthly convention).
            if (i > 0 && monthContexts[0].SourceAvailable == false)
            {
                monthContexts.Add(UnavailableMonthContext(month, monthContexts[0].SourceUnavailableReason));
                continue;
            }

            var result = await _source.GetAsync(
                new AccountDailyMetricQuery { Start = month.Start, EndExclusive = month.End.AddDays(1) },
                cancellationToken).ConfigureAwait(false);

            if (!result.IsAvailable || result.Dataset is null)
            {
                var reason = string.IsNullOrWhiteSpace(result.UnavailableReason)
                    ? "Sumber analytics level akun (harian) belum terintegrasi atau belum dapat dihubungi"
                    : result.UnavailableReason!;
                monthContexts.Add(UnavailableMonthContext(month, reason));
                continue;
            }

            monthContexts.Add(BuildMonthContext(month, result.Dataset));
        }

        return BuildPeriodContext(monthContexts);
    }

    private static MonthContext BuildMonthContext(MonthlyBucket month, AccountDailyMetricDataset dataset)
    {
        var daysInMonth = month.DayCount;
        var availableKeys = dataset.AvailableMetricKeys;
        var limitedReason = LimitedReasonFor(availableKeys);

        // Aggregate daily records into monthly totals
        var records = new Dictionary<DateTime, AccountDailyMetricRecord>();
        foreach (var record in dataset.Records)
        {
            var date = record.Date.Date;
            if (date < month.Start || date > month.End || records.ContainsKey(date)) continue;
            records[date] = record;
        }

        var reachDays = new List<long?>(daysInMonth);
        var videoViewsDays = new List<long?>(daysInMonth);
        var profileViewsDays = new List<long?>(daysInMonth);
        var likesDays = new List<long?>(daysInMonth);
        var commentsDays = new List<long?>(daysInMonth);
        var sharesDays = new List<long?>(daysInMonth);
        var interactionsDays = new List<long?>(daysInMonth);
        var netFollowerDays = new List<long?>(daysInMonth);

        for (var day = month.Start; day <= month.End; day = day.AddDays(1))
        {
            records.TryGetValue(day, out var record);
            reachDays.Add(record?.Reach);
            videoViewsDays.Add(record?.VideoViews);
            profileViewsDays.Add(record?.ProfileViews);
            likesDays.Add(record?.Likes);
            commentsDays.Add(record?.Comments);
            sharesDays.Add(record?.Shares);
            interactionsDays.Add(record is null
                ? null
                : DailyReportCalculator.Interactions(record.Likes, record.Comments, record.Shares));
            netFollowerDays.Add(record is null
                ? null
                : DailyReportCalculator.NetFollower(record.FollowersGained, record.FollowersLost));
        }

        var reachAgg = availableKeys is null || availableKeys.Contains(DailyReportMetrics.Reach)
            ? DailyReportCalculator.Sum(reachDays, daysInMonth)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var videoViewsAgg = availableKeys is null || availableKeys.Contains(DailyReportMetrics.VideoViews)
            ? DailyReportCalculator.Sum(videoViewsDays, daysInMonth)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var profileViewsAgg = availableKeys is null || availableKeys.Contains(DailyReportMetrics.ProfileViews)
            ? DailyReportCalculator.Sum(profileViewsDays, daysInMonth)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var netFollowerAgg = availableKeys is null || availableKeys.Contains(DailyReportMetrics.NetFollower)
            ? DailyReportCalculator.Sum(netFollowerDays, daysInMonth)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);

        var windowed = dataset.PeriodInteractions;
        var hasWindowedInteractions = windowed is not null
            && windowed.Likes.HasValue && windowed.Comments.HasValue && windowed.Shares.HasValue;

        DailyMetricAggregate likesAgg, commentsAgg, sharesAgg, interactionsAgg;
        if (availableKeys is not null && !availableKeys.Contains(DailyReportMetrics.Interactions))
        {
            likesAgg = commentsAgg = sharesAgg = interactionsAgg =
                DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        }
        else if (hasWindowedInteractions)
        {
            likesAgg = new DailyMetricAggregate(windowed!.Likes.Value, daysInMonth, DailyMetricStatus.Complete);
            commentsAgg = new DailyMetricAggregate(windowed.Comments.Value, daysInMonth, DailyMetricStatus.Complete);
            sharesAgg = new DailyMetricAggregate(windowed.Shares.Value, daysInMonth, DailyMetricStatus.Complete);
            interactionsAgg = new DailyMetricAggregate(
                windowed.Likes.Value + windowed.Comments.Value + windowed.Shares.Value,
                daysInMonth, DailyMetricStatus.Complete);
        }
        else
        {
            likesAgg = DailyReportCalculator.Sum(likesDays, daysInMonth);
            commentsAgg = DailyReportCalculator.Sum(commentsDays, daysInMonth);
            sharesAgg = DailyReportCalculator.Sum(sharesDays, daysInMonth);
            interactionsAgg = DailyReportCalculator.Sum(interactionsDays, daysInMonth);
        }

        var engagementRate = DailyReportCalculator.EngagementRate(interactionsAgg.Value, reachAgg.Value);

        var snapshot = availableKeys is null || availableKeys.Contains(DailyReportMetrics.TotalFollower)
            ? DailyReportCalculator.FollowerSnapshotAtOrBefore(
                dataset.FollowerSnapshots.Where(s => s.AsOfDate.Date >= month.Start).ToList(),
                month.End)
            : null;

        return new MonthContext
        {
            Month = month,
            SourceAvailable = true,
            SourceName = dataset.SourceName,
            AvailableMetricKeys = availableKeys is not null ? new HashSet<string>(availableKeys) : null,
            LimitedSourceReason = limitedReason,
            Reach = reachAgg,
            VideoViews = videoViewsAgg,
            ProfileViews = profileViewsAgg,
            Likes = likesAgg,
            Comments = commentsAgg,
            Shares = sharesAgg,
            Interactions = interactionsAgg,
            NetFollower = netFollowerAgg,
            EngagementRate = engagementRate,
            FollowerSnapshot = snapshot,
            ViewerComposition = dataset.ViewerComposition
        };
    }

    private static string? LimitedReasonFor(IReadOnlySet<string>? availableKeys)
    {
        if (availableKeys is null) return null;
        if (!availableKeys.Contains(DailyReportMetrics.Reach)) return "Reach tidak tersedia dari sumber yang terverifikasi";
        if (!availableKeys.Contains(DailyReportMetrics.VideoViews)) return "Video Views tidak tersedia dari sumber yang terverifikasi";
        if (!availableKeys.Contains(DailyReportMetrics.ProfileViews)) return "Profile Views tidak tersedia dari sumber yang terverifikasi";
        if (!availableKeys.Contains(DailyReportMetrics.Interactions)) return "Interactions tidak tersedia dari sumber yang terverifikasi";
        if (!availableKeys.Contains(DailyReportMetrics.NetFollower)) return "Net Follower tidak tersedia dari sumber yang terverifikasi";
        if (!availableKeys.Contains(DailyReportMetrics.TotalFollower)) return "Total Follower tidak tersedia dari sumber yang terverifikasi";
        return null;
    }

    private static PeriodContext BuildPeriodContext(List<MonthContext> monthContexts)
    {
        var availableMonths = monthContexts.Where(m => m.SourceAvailable).ToList();
        var sourceAvailable = availableMonths.Count > 0;

        var sourceUnavailableReason = sourceAvailable
            ? string.Empty
            : monthContexts.Select(m => m.SourceUnavailableReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
              ?? "Sumber analytics level akun (harian) belum terintegrasi atau belum dapat dihubungi";

        var firstAvailable = availableMonths.FirstOrDefault();
        var availableKeys = firstAvailable?.AvailableMetricKeys;
        var limitedReason = firstAvailable?.LimitedSourceReason;

        // Aggregate across months: sum of month values. Missing buckets are NOT zero.
        DailyMetricAggregate Aggregate(Func<MonthContext, DailyMetricAggregate> selector)
        {
            long? sum = null; var covered = 0; var complete = 0; var anyUnavailable = false;
            foreach (var m in monthContexts)
            {
                var aggregate = selector(m);
                if (aggregate.Status == DailyMetricStatus.Unavailable) { anyUnavailable = true; continue; }
                if (aggregate.Value is null) continue;
                sum = (sum ?? 0L) + aggregate.Value.Value; covered++;
                if (aggregate.Status == DailyMetricStatus.Complete) complete++;
            }
            if (covered == 0) return DailyMetricAggregate.None(
                anyUnavailable ? DailyMetricStatus.Unavailable : DailyMetricStatus.NoData);
            var status = complete == monthContexts.Count ? DailyMetricStatus.Complete : DailyMetricStatus.Partial;
            return new DailyMetricAggregate(sum, covered, status);
        }

        var reachAgg = Aggregate(m => m.Reach);
        var videoViewsAgg = Aggregate(m => m.VideoViews);
        var profileViewsAgg = Aggregate(m => m.ProfileViews);
        var likesAgg = Aggregate(m => m.Likes);
        var commentsAgg = Aggregate(m => m.Comments);
        var sharesAgg = Aggregate(m => m.Shares);
        var interactionsAgg = Aggregate(m => m.Interactions);
        var netFollowerAgg = Aggregate(m => m.NetFollower);

        var monthsInPeriod = monthContexts.Count;

        if (!sourceAvailable)
        {
            return new PeriodContext
            {
                SourceAvailable = false,
                SourceUnavailableReason = sourceUnavailableReason,
                SourceName = firstAvailable?.SourceName ?? string.Empty,
                AvailableMetricKeys = availableKeys,
                LimitedSourceReason = limitedReason,
                Reach = reachAgg, VideoViews = videoViewsAgg, ProfileViews = profileViewsAgg,
                Likes = likesAgg, Comments = commentsAgg, Shares = sharesAgg,
                Interactions = interactionsAgg, NetFollower = netFollowerAgg,
                EngagementRate = new PeriodValue(null, DailyMetricStatus.Unavailable, sourceUnavailableReason),
                TotalFollower = new PeriodValue(null, DailyMetricStatus.Unavailable, sourceUnavailableReason)
            };
        }

        // Period ER = Total Interactions / Total Reach x 100 (never the average of monthly ERs).
        PeriodValue engagementRate;
        if (interactionsAgg.Value is null || reachAgg.Value is null)
        {
            var status = DailyReportCalculator.Worst(interactionsAgg.Status, reachAgg.Status);
            var erReason = reachAgg.Status == DailyMetricStatus.Unavailable
                ? ERRequiresReachReason
                : status == DailyMetricStatus.Unavailable ? (limitedReason ?? sourceUnavailableReason) : NoDataPeriodReason;
            engagementRate = new PeriodValue(null, status, erReason);
        }
        else if (reachAgg.Value <= 0)
        {
            engagementRate = new PeriodValue(null, DailyMetricStatus.NoData,
                "Engagement Rate tidak dapat dihitung karena Reach periode ini tidak tersedia atau 0.");
        }
        else
        {
            engagementRate = new PeriodValue(
                DailyReportCalculator.PeriodEngagementRate(interactionsAgg.Value, reachAgg.Value),
                DailyReportCalculator.Worst(interactionsAgg.Status, reachAgg.Status), null);
        }

        // Total Follower: latest snapshot inside period
        var snapshot = monthContexts.Select(m => m.FollowerSnapshot)
            .Where(s => s is not null).OrderByDescending(s => s!.AsOfDate).ThenByDescending(s => s!.CapturedAt).FirstOrDefault();

        var totalFollower = monthContexts[0].Supplies(DailyReportMetrics.TotalFollower)
            ? snapshot is null
                ? new PeriodValue(null, DailyMetricStatus.NoData,
                    "Tidak ada snapshot follower yang valid pada periode ini.")
                : new PeriodValue(snapshot!.FollowerCount, DailyMetricStatus.Complete, null)
            : new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason);

    return new PeriodContext
    {
        SourceAvailable = true,
        SourceName = firstAvailable?.SourceName ?? string.Empty,
        AvailableMetricKeys = availableKeys,
        LimitedSourceReason = limitedReason,
        Months = monthContexts,
        Reach = reachAgg, VideoViews = videoViewsAgg,
        ProfileViews = profileViewsAgg, Likes = likesAgg, Comments = commentsAgg,
        Shares = sharesAgg, Interactions = interactionsAgg, NetFollower = netFollowerAgg,
        EngagementRate = engagementRate, TotalFollower = totalFollower
    };
}

    private static MonthContext UnavailableMonthContext(MonthlyBucket month, string reason)
        => new MonthContext
        {
            Month = month,
            SourceAvailable = false,
            SourceUnavailableReason = reason,
            SourceName = string.Empty,
            AvailableMetricKeys = null,
            LimitedSourceReason = reason,
            Reach = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            VideoViews = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            ProfileViews = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Likes = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Comments = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Shares = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Interactions = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            NetFollower = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            EngagementRate = new PeriodValue(null, DailyMetricStatus.Unavailable, reason).Value,
            FollowerSnapshot = null,
            ViewerComposition = null
        };

    private static PeriodContext BuildPreviousPeriodContext(List<MonthlyBucket> months, PeriodContext currentPeriod)
    {
        if (!currentPeriod.SourceAvailable)
        {
        return new PeriodContext
        {
            SourceAvailable = false,
            SourceUnavailableReason = currentPeriod.SourceUnavailableReason,
            SourceName = currentPeriod.SourceName,
            AvailableMetricKeys = currentPeriod.AvailableMetricKeys,
            LimitedSourceReason = currentPeriod.LimitedSourceReason,
            Months = currentPeriod.Months,
            Reach = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            VideoViews = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            ProfileViews = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Likes = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Comments = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Shares = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Interactions = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            NetFollower = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            EngagementRate = new PeriodValue(null, DailyMetricStatus.Unavailable, currentPeriod.SourceUnavailableReason),
            TotalFollower = new PeriodValue(null, DailyMetricStatus.Unavailable, currentPeriod.SourceUnavailableReason)
        };
    } // Simplified previous period
        return new PeriodContext
        {
            SourceAvailable = false,
            Months = new List<MonthContext>(),

            SourceUnavailableReason = "Periode pembanding tidak tersedia sehingga kolom Periode Sebelumnya dan Selisih ditampilkan \"-\".",
            SourceName = string.Empty,
            AvailableMetricKeys = null,
            LimitedSourceReason = null,
            Reach = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            VideoViews = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            ProfileViews = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Likes = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Comments = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Shares = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Interactions = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            NetFollower = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            EngagementRate = new PeriodValue(null, DailyMetricStatus.Unavailable, "Periode pembanding tidak tersedia"),
            TotalFollower = new PeriodValue(null, DailyMetricStatus.Unavailable, "Periode pembanding tidak tersedia")
        };
    }

    private static IReadOnlyList<DailyReportMetric> BuildCards(PeriodContext current, PeriodContext previous, string? limitedReason)
    {
        PeriodValue Cur(DailyMetricAggregate agg) => ToValue(agg, current.MonthsInPeriod, limitedReason);
        PeriodValue Prev(DailyMetricAggregate agg) => ToValue(agg, previous.MonthsInPeriod, limitedReason);

        var cards = new List<DailyReportMetric>
        {
            Build(DailyReportMetrics.Reach, "Reach", Cur(current.Reach), Prev(previous.Reach), DailyMetricDeltaKind.Absolute),
            Build(DailyReportMetrics.VideoViews, "Video Views", Cur(current.VideoViews), Prev(previous.VideoViews), DailyMetricDeltaKind.Absolute),
            Build(DailyReportMetrics.ProfileViews, "Profile Views", Cur(current.ProfileViews), Prev(previous.ProfileViews), DailyMetricDeltaKind.Absolute),
            Build(DailyReportMetrics.Interactions, "Interactions", Cur(current.Interactions), Prev(previous.Interactions), DailyMetricDeltaKind.Absolute),
            Build(DailyReportMetrics.NetFollower, "Net Follower", Cur(current.NetFollower), Prev(previous.NetFollower), DailyMetricDeltaKind.Absolute)
        };
        cards.Insert(4, Build(
            DailyReportMetrics.EngagementRate, "Engagement Rate",
            new PeriodValue(current.EngagementRate!.Value, DailyReportCalculator.Worst(current.EngagementRate!.Status, previous.EngagementRate!.Status), null),
            new PeriodValue(previous.EngagementRate!.Value, DailyReportCalculator.Worst(current.EngagementRate!.Status, previous.EngagementRate!.Status), null),
            DailyMetricDeltaKind.PercentagePoints));
        cards.Add(Build(
            DailyReportMetrics.TotalFollower, "Total Follower",
            current.TotalFollower!,
            previous.TotalFollower!,
            DailyMetricDeltaKind.PositionOnly, showInComparison: false));
        return cards;
    }

    private static PeriodValue ToValue(DailyMetricAggregate agg, int monthsInPeriod, string? limitedReason)
    {
        var value = agg.Value;
        var status = agg.Status;
        var daysCovered = agg.DaysCovered;
        var reason = status switch
        {
            DailyMetricStatus.Unavailable => limitedReason ?? "Sumber data tidak tersedia",
            DailyMetricStatus.NoData => NoDataPeriodReason,
            _ => null
        };
        return new PeriodValue(value, status, reason, daysCovered, monthsInPeriod);
    }

    private static DailyReportMetric Build(string key, string label, PeriodValue current, PeriodValue previous, DailyMetricDeltaKind deltaKind, bool showInComparison = true)
    {
        decimal? difference = null, growth = null;
        if (current.Value.HasValue && previous.Value.HasValue)
        {
            difference = DailyReportCalculator.Difference(current.Value, previous.Value);
            if (deltaKind == DailyMetricDeltaKind.Absolute) growth = DailyReportCalculator.GrowthPercentage(current.Value, previous.Value);
        }
        var reason = current.Reason ?? (current.Value is null ? NoDataPeriodReason : null);
        return new DailyReportMetric
        {
            Key = key, Label = label, CurrentValue = current.Value, PreviousValue = previous.Value,
            Difference = difference, GrowthPercentage = growth, DeltaKind = deltaKind,
            CurrentStatus = current.Status, PreviousStatus = previous.Status,
            UnavailableReason = reason, CoverageNote = current.CoverageNote, ShowInComparison = showInComparison
        };
    }

    private static IReadOnlyList<MonthlyReportRow> BuildTrendRows(PeriodContext context)
    {
        var rows = new List<MonthlyReportRow>(context.Months.Count);
        foreach (var monthContext in context.Months)
        {
            var month = monthContext.Month; var sourceAvailable = monthContext.SourceAvailable;
            var reach = sourceAvailable ? monthContext.Reach.Value : null;
            var interactions = sourceAvailable ? monthContext.Interactions.Value : null;
            var er = sourceAvailable && reach.HasValue && interactions.HasValue
                ? DailyReportCalculator.EngagementRate(interactions.Value, reach.Value)
                : null;

            var states = new Dictionary<string, MonthlyMetricState>(StringComparer.Ordinal)
            {
                [DailyReportMetrics.Reach] = new MonthlyMetricState
                {
                    Status = monthContext.Reach.Status,
                    DaysCovered = monthContext.Reach.DaysCovered, DaysInMonth = month.DayCount,
                    Reason = monthContext.Reach.Status switch
                    {
                        DailyMetricStatus.Unavailable => sourceAvailable ? monthContext.LimitedSourceReason ?? monthContext.SourceUnavailableReason : monthContext.SourceUnavailableReason,
                        DailyMetricStatus.NoData => NoDataMonthReason, _ => null
                    }
                },
                [DailyReportMetrics.VideoViews] = new MonthlyMetricState
                {
                    Status = monthContext.VideoViews.Status,
                    DaysCovered = monthContext.VideoViews.DaysCovered, DaysInMonth = month.DayCount,
                    Reason = monthContext.VideoViews.Status switch
                    {
                        DailyMetricStatus.Unavailable => sourceAvailable ? monthContext.LimitedSourceReason ?? monthContext.SourceUnavailableReason : monthContext.SourceUnavailableReason,
                        DailyMetricStatus.NoData => NoDataMonthReason, _ => null
                    }
                },
                [DailyReportMetrics.ProfileViews] = new MonthlyMetricState
                {
                    Status = monthContext.ProfileViews.Status,
                    DaysCovered = monthContext.ProfileViews.DaysCovered, DaysInMonth = month.DayCount,
                    Reason = monthContext.ProfileViews.Status switch
                    {
                        DailyMetricStatus.Unavailable => sourceAvailable ? monthContext.LimitedSourceReason ?? monthContext.SourceUnavailableReason : monthContext.SourceUnavailableReason,
                        DailyMetricStatus.NoData => NoDataMonthReason, _ => null
                    }
                },
                [DailyReportMetrics.Interactions] = new MonthlyMetricState
                {
                    Status = monthContext.Interactions.Status,
                    DaysCovered = monthContext.Interactions.DaysCovered, DaysInMonth = month.DayCount,
                    Reason = monthContext.Interactions.Status switch
                    {
                        DailyMetricStatus.Unavailable => sourceAvailable ? monthContext.LimitedSourceReason ?? monthContext.SourceUnavailableReason : monthContext.SourceUnavailableReason,
                        DailyMetricStatus.NoData => NoDataMonthReason, _ => null
                    }
                },
                [DailyReportMetrics.NetFollower] = new MonthlyMetricState
                {
                    Status = monthContext.NetFollower.Status,
                    DaysCovered = monthContext.NetFollower.DaysCovered, DaysInMonth = month.DayCount,
                    Reason = monthContext.NetFollower.Status switch
                    {
                        DailyMetricStatus.Unavailable => sourceAvailable ? monthContext.LimitedSourceReason ?? monthContext.SourceUnavailableReason : monthContext.SourceUnavailableReason,
                        DailyMetricStatus.NoData => NoDataMonthReason, _ => null
                    }
                }
            };

            rows.Add(new MonthlyReportRow
            {
                Start = month.Start, End = month.End, DayCount = month.DayCount, IsPartial = month.IsPartial,
                Reach = sourceAvailable ? monthContext.Reach.Value : null,
                VideoViews = sourceAvailable ? monthContext.VideoViews.Value : null,
                ProfileViews = sourceAvailable ? monthContext.ProfileViews.Value : null,
                Interactions = sourceAvailable ? monthContext.Interactions.Value : null,
                EngagementRate = er,
                NetFollower = sourceAvailable ? monthContext.NetFollower.Value : null,
                MetricStates = states
            });
        }
        return rows;
    }

    private static DailyReportComposition BuildInteractionComposition(PeriodContext context)
    {
        if (!context.SourceAvailable) return UnavailableComp("interaction_composition", "Komposisi Interactions", context.SourceUnavailableReason);
        if (!context.Supplies(DailyReportMetrics.Interactions))
            return UnavailableComp("interaction_composition", "Komposisi Interactions",
                context.LimitedSourceReason ?? context.SourceUnavailableReason);

        var total = context.Interactions.Value;
        var statuses = new[] { context.Likes.Status, context.Comments.Status, context.Shares.Status, context.Interactions.Status };
        if (!DailyReportCalculator.CanRenderComposition(statuses, total))
        {
            var partial = statuses.Any(s => s == DailyMetricStatus.Partial);
            var unavailable = statuses.Any(s => s == DailyMetricStatus.Unavailable);
            var reason = partial
                ? "Komponen interaksi tidak lengkap pada periode ini - komposisi tidak ditampilkan agar tidak menyesatkan."
                : unavailable ? (context.LimitedSourceReason ?? context.SourceUnavailableReason)
                    : "Belum ada data tercatat pada periode ini.";
            return UnavailableComp("interaction_composition", "Komposisi Interactions", reason);
        }
        return new DailyReportComposition { Key = "interaction_composition", Title = "Komposisi Interactions", IsAvailable = true, Total = total, Segments = InteractionSegments(context, total) };
    }

    private static IReadOnlyList<DailyReportCompositionSegment> InteractionSegments(PeriodContext context, decimal? total) => new[]
    {
        Segment("like", "Like", "#4d9fff", context.Likes.Value, total),
        Segment("comment", "Comment", "#e8b83f", context.Comments.Value, total),
        Segment("share", "Share", "#a975e8", context.Shares.Value, total)
    };

    private static DailyReportComposition BuildViewerComposition(PeriodContext context)
    {
        if (!context.SourceAvailable) return UnavailableComp("viewer_composition", "Komposisi Reach · Viewer Baru vs Lama", context.SourceUnavailableReason);

        // Get ViewerComposition from the months in the period
        AccountViewerComposition? viewers = null;
        if (context.Months.Count > 0)
        {
            // Try to find a month with ViewerComposition available
            var monthWithViewer = context.Months.FirstOrDefault(m => m.ViewerComposition is not null);
            if (monthWithViewer.ViewerComposition is not null)
            {
                viewers = monthWithViewer.ViewerComposition;
            }
        }

        if (viewers is null || viewers.NewViewers is null || viewers.ReturningViewers is null)
        {
            return UnavailableComp("viewer_composition", "Komposisi Reach · Viewer Baru vs Lama",
                context.Months.Count > 1
                    ? "Komposisi viewer baru/lama hanya dapat ditampilkan untuk rentang yang dicakup satu bulan."
                    : "Komposisi viewer baru/lama level akun belum tersedia dari sumber yang terverifikasi.");
        }
        var total = viewers!.NewViewers.Value + viewers!.ReturningViewers.Value;
        if (total <= 0) return UnavailableComp("viewer_composition", "Komposisi Reach · Viewer Baru vs Lama", "Total viewer periode ini 0 atau tidak tersedia.");
        return new DailyReportComposition { Key = "viewer_composition", Title = "Komposisi Reach · Viewer Baru vs Lama", IsAvailable = true, Total = total, Segments = ViewerSegments(viewers!.NewViewers.Value, viewers!.ReturningViewers.Value) };
    }

    private static IReadOnlyList<DailyReportCompositionSegment> ViewerSegments(long? newViewers, long? returningViewers)
    {
        decimal? total = newViewers is null || returningViewers is null ? null : newViewers.Value + returningViewers.Value;
        return new[] { Segment("new", "Viewer Baru", "#4ade80", newViewers, total), Segment("returning", "Viewer Lama", "#7c8cff", returningViewers, total) };
    }

    private static DailyReportCompositionSegment Segment(string key, string label, string color, long? value, decimal? total)
        => new() { Key = key, Label = label, Color = color, Value = value, Percentage = DailyReportCalculator.CompositionPercentage(value, total) };

    private static DailyReportComposition UnavailableComp(string key, string title, string reason)
        => new() { Key = key, Title = title, IsAvailable = false, UnavailableReason = reason, Segments = null };

    private static IReadOnlyList<string> BuildNotices(PeriodContext current, PeriodContext previous, IReadOnlyList<DailyReportMetric> cards)
    {
        var notices = new List<string>();
        if (!current.SourceAvailable)
        {
            var reason = current.SourceUnavailableReason ?? "Sumber analytics level akun (harian) belum terintegrasi atau belum dapat dihubungi";
            notices.Add("Seluruh metrik ditampilkan \"-\" karena " + reason);
        }
        else if (!previous.SourceAvailable)
        {
            notices.Add("Periode pembanding tidak tersedia sehingga kolom Periode Sebelumnya dan Selisih ditampilkan \"-\".");
        }
        return notices;
    }

    private static IReadOnlyList<DailyReportSourceNote> BuildSourceNotes(PeriodContext currentPeriod)
    {
        var notes = new List<DailyReportSourceNote>();
        if (!currentPeriod.SourceAvailable)
        {
            notes.Add(new DailyReportSourceNote { Metric = "Semua", Definition = "Sumber analytics level akun belum terintegrasi", Status = currentPeriod.SourceUnavailableReason });
        }
        else
        {
            if (currentPeriod.AvailableMetricKeys is not null)
            {
                var availableLabels = CardMetrics.Where(m => currentPeriod.AvailableMetricKeys.Contains(m.Key)).Select(m => m.Label).ToList();
                if (availableLabels.Count > 0)
                    notes.Add(new DailyReportSourceNote { Metric = "Metrik tersedia", Definition = "Sumber data menyediakan: " + string.Join(", ", availableLabels), Status = "tersedia" });
            }
            if (currentPeriod.Interactions is not null && currentPeriod.Interactions.Value.HasValue)
                notes.Add(new DailyReportSourceNote { Metric = "Interactions", Definition = "Like + Comment + Share dari endpoint shop video performance overview", Status = "tersedia" });
        }
        return notes;
    }

    // ---- Helper types ----

    private sealed class MonthContext
    {
        public required MonthlyBucket Month { get; init; }
        public bool SourceAvailable { get; init; } = false;
        public string SourceUnavailableReason { get; set; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;
        public IReadOnlySet<string>? AvailableMetricKeys { get; init; }
        public string? LimitedSourceReason { get; init; }
        public bool Supplies(string key) => AvailableMetricKeys is null || AvailableMetricKeys.Contains(key);
        public DailyMetricAggregate Reach { get; init; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate VideoViews { get; init; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate ProfileViews { get; init; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate Likes { get; init; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate Comments { get; init; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate Shares { get; init; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate Interactions { get; init; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate NetFollower { get; init; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public decimal? EngagementRate { get; init; }
        public FollowerSnapshot? FollowerSnapshot { get; init; }
        public AccountViewerComposition? ViewerComposition { get; init; }
    }

    private sealed class PeriodContext
    {
        public bool SourceAvailable { get; set; }
        public string SourceUnavailableReason { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public IReadOnlySet<string>? AvailableMetricKeys { get; set; }
        public string? LimitedSourceReason { get; set; }
        public int MonthsInPeriod { get; set; }
        public IReadOnlyList<MonthContext> Months { get; set; } = new List<MonthContext>();
        public bool Supplies(string key) => AvailableMetricKeys is null || AvailableMetricKeys.Contains(key);
        public DailyMetricAggregate Reach { get; set; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate VideoViews { get; set; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate ProfileViews { get; set; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate Likes { get; set; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate Comments { get; set; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate Shares { get; set; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate Interactions { get; set; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public DailyMetricAggregate NetFollower { get; set; } = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        public PeriodValue EngagementRate { get; set; } = new PeriodValue();
        public PeriodValue TotalFollower { get; set; } = new PeriodValue();
    }

    private sealed class PeriodValue
    {
        public PeriodValue() { }

        public PeriodValue(decimal? value, DailyMetricStatus status, string? reason)
        {
            Value = value; Status = status; Reason = reason;
        }

        public PeriodValue(decimal? value, DailyMetricStatus status, string? reason, int monthsCovered, int monthsInPeriod)
        {
            Value = value; Status = status; Reason = reason;
            MonthsCovered = monthsCovered; MonthsInPeriod = monthsInPeriod;
        }

        public decimal? Value { get; set; }
        public DailyMetricStatus Status { get; set; }
        public string? Reason { get; set; }
        public int MonthsCovered { get; set; }
        public int MonthsInPeriod { get; set; }

        public string? CoverageNote
        {
            get
            {
                if (Status == DailyMetricStatus.Partial && MonthsCovered < MonthsInPeriod)
                    return $"Data parsial: {MonthsCovered} dari {MonthsInPeriod} bulan tercatat.";
                if (Status == DailyMetricStatus.Partial)
                    return "Data parsial: seluruh bulan tercatat namun sebagian hari di dalamnya tidak tercatat.";
                return null;
            }
        }
    }
}