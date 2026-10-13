using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Services;

namespace KaleContentOps.Services.CreativePerformance;

/// <summary>
/// Creative Performance - Daily Report service.
///
/// Single place where every number of the page is produced: the two periods, the
/// seven summary cards, the comparison rows, the daily rows used by BOTH charts and
/// the detail table, and the two donut compositions. Nothing here formats for
/// display (that is the View's job) and nothing here invents a value: a metric with
/// no verified source stays null and carries an Unavailable reason.
///
/// Data source: <see cref="IAccountDailyMetricSource"/>. The provider registered in
/// DI today is <see cref="UnavailableAccountDailyMetricSource"/> (BLOCKED - only
/// per-video metrics exist in this workspace), so the page renders honest empty
/// states until an account-level integration is verified. Swapping the DI
/// registration to a verified EF Core / TikTok account provider activates every
/// section without touching this class or the UI.
/// </summary>
public sealed class DailyPerformanceService : IDailyPerformanceService
{
    private const string NoDataReason = "Belum ada data tercatat pada periode ini.";

    private readonly IAccountDailyMetricSource _source;
    private readonly IShopTimeZone _shopTimeZone;

    public DailyPerformanceService(IAccountDailyMetricSource source, IShopTimeZone shopTimeZone)
    {
        _source = source;
        _shopTimeZone = shopTimeZone;
    }

    public async Task<DailyReportData> BuildAsync(DailyReportFilter filter, CancellationToken cancellationToken = default)
    {
        // Validated inclusive range; EndDate < StartDate throws (the controller turns
        // that into a 400). The user's dates are never shifted or snapped.
        var current = DailyReportDateRange.Create(
            filter.StartDate, filter.EndDate, "StartDate", "EndDate");
        var previous = current.Previous();

        var currentResult = await _source.GetAsync(
            new AccountDailyMetricQuery { Start = current.Start, EndExclusive = current.EndExclusive },
            cancellationToken);

        // No point asking the provider twice when it cannot answer at all.
        var previousResult = currentResult.IsAvailable
            ? await _source.GetAsync(
                new AccountDailyMetricQuery { Start = previous.Start, EndExclusive = previous.EndExclusive },
                cancellationToken)
            : currentResult;

        var currentContext = BuildContext(current, currentResult);
        var previousContext = BuildContext(previous, previousResult);

        var cards = BuildCards(currentContext, previousContext);
        var rows = BuildRows(currentContext);

        var interactionComposition = BuildInteractionComposition(currentContext);
        var viewerComposition = BuildViewerComposition(currentContext);

        return new DailyReportData
        {
            CurrentStart = current.Start,
            CurrentEnd = current.End,
            PreviousStart = previous.Start,
            PreviousEnd = previous.End,
            CurrentDayCount = current.DayCount,
            PreviousDayCount = previous.DayCount,
            TimeZoneId = _shopTimeZone.TimeZoneId,
            TimeZoneLabel = _shopTimeZone.TimeZoneId == ShopTimeZoneOptions.DefaultTimeZoneId
                ? "GMT+7"
                : _shopTimeZone.TimeZoneId,
            Cards = cards,
            ComparisonRows = cards.Where(c => c.ShowInComparison).ToList(),
            // Same row objects, opposite order: charts read ascending, the table descending.
            Trend = rows,
            DailyRows = rows.AsEnumerable().Reverse().ToList(),
            InteractionComposition = interactionComposition,
            ViewerComposition = viewerComposition,
            Notices = BuildNotices(currentResult, previousResult, cards),
            SourceNotes = BuildSourceNotes(currentResult),
            HasAnyMetricData = cards.Any(c => c.CurrentValue.HasValue)
        };
    }

    // ------------------------------------------------------------------
    // Period evaluation
    // ------------------------------------------------------------------

    /// <summary>All aggregates of one period, evaluated once and reused everywhere.</summary>
    private sealed class PeriodContext
    {
        public DailyReportDateRange Range { get; init; }
        public bool SourceAvailable { get; init; }
        public string SourceUnavailableReason { get; init; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;
        public int DaysInPeriod { get; init; }

        public DailyMetricAggregate Reach { get; init; }
        public DailyMetricAggregate VideoViews { get; init; }
        public DailyMetricAggregate ProfileViews { get; init; }
        public DailyMetricAggregate Likes { get; init; }
        public DailyMetricAggregate Comments { get; init; }
        public DailyMetricAggregate Shares { get; init; }
        public DailyMetricAggregate Interactions { get; init; }
        public DailyMetricAggregate NetFollower { get; init; }

        public PeriodValue? EngagementRate { get; init; }
        public PeriodValue? TotalFollower { get; init; }
        public AccountViewerComposition? ViewerComposition { get; init; }
        public IReadOnlyDictionary<DateTime, AccountDailyMetricRecord> RecordsByDate { get; init; }
            = new Dictionary<DateTime, AccountDailyMetricRecord>();
    }

    /// <summary>One period's value for one metric, with completeness and a display-ready reason.</summary>
    private sealed record PeriodValue(
        decimal? Value,
        DailyMetricStatus Status,
        string? Reason,
        int DaysCovered,
        int DaysInPeriod)
    {
        public string? CoverageNote => Status == DailyMetricStatus.Partial
            ? $"Data parsial: {DaysCovered} dari {DaysInPeriod} hari tercatat."
            : null;
    }

    private static PeriodContext BuildContext(DailyReportDateRange range, AccountDailyMetricResult result)
    {
        var days = range.Days().ToList();

        if (!result.IsAvailable || result.Dataset is null)
        {
            var reason = string.IsNullOrWhiteSpace(result.UnavailableReason)
                ? UnavailableAccountDailyMetricSource.Reason
                : result.UnavailableReason!;
            var none = DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
            return new PeriodContext
            {
                Range = range,
                DaysInPeriod = days.Count,
                SourceAvailable = false,
                SourceUnavailableReason = reason,
                Reach = none,
                VideoViews = none,
                ProfileViews = none,
                Likes = none,
                Comments = none,
                Shares = none,
                Interactions = none,
                NetFollower = none,
                EngagementRate = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                TotalFollower = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count)
            };
        }

        // One lookup per calendar day: days WITHOUT a record stay null (never 0).
        var records = new Dictionary<DateTime, AccountDailyMetricRecord>();
        foreach (var record in result.Dataset.Records)
        {
            var date = record.Date.Date;
            if (date < range.Start || date > range.End || records.ContainsKey(date))
            {
                continue;
            }

            records[date] = record;
        }

        var reach = new List<long?>(days.Count);
        var videoViews = new List<long?>(days.Count);
        var profileViews = new List<long?>(days.Count);
        var likes = new List<long?>(days.Count);
        var comments = new List<long?>(days.Count);
        var shares = new List<long?>(days.Count);
        var interactions = new List<long?>(days.Count);
        var netFollower = new List<long?>(days.Count);

        foreach (var day in days)
        {
            var record = records.GetValueOrDefault(day);

            reach.Add(record?.Reach);
            videoViews.Add(record?.VideoViews);
            profileViews.Add(record?.ProfileViews);
            likes.Add(record?.Likes);
            comments.Add(record?.Comments);
            shares.Add(record?.Shares);
            // Composite metrics: a record missing any component yields null, never a partial total.
            interactions.Add(record is null
                ? null
                : DailyReportCalculator.Interactions(record.Likes, record.Comments, record.Shares));
            netFollower.Add(record is null
                ? null
                : DailyReportCalculator.NetFollower(record.FollowersGained, record.FollowersLost));
        }

        var sourceReason = string.Empty;
        var reachAgg = DailyReportCalculator.Sum(reach, days.Count);
        var interactionsAgg = DailyReportCalculator.Sum(interactions, days.Count);

        // Period ER = Total Interactions / Total Reach x 100 (never the average of daily ERs).
        PeriodValue engagementRate;
        if (interactionsAgg.Value is null || reachAgg.Value is null)
        {
            var status = DailyReportCalculator.Worst(interactionsAgg.Status, reachAgg.Status);
            engagementRate = new PeriodValue(
                null,
                status,
                status == DailyMetricStatus.Unavailable ? sourceReason : NoDataReason,
                0,
                days.Count);
        }
        else if (reachAgg.Value <= 0)
        {
            engagementRate = new PeriodValue(
                null,
                DailyMetricStatus.NoData,
                "Engagement Rate tidak dapat dihitung karena Reach periode ini tidak tersedia atau 0.",
                0,
                days.Count);
        }
        else
        {
            engagementRate = new PeriodValue(
                DailyReportCalculator.PeriodEngagementRate(interactionsAgg.Value, reachAgg.Value),
                DailyReportCalculator.Worst(interactionsAgg.Status, reachAgg.Status),
                null,
                Math.Min(interactionsAgg.DaysCovered, reachAgg.DaysCovered),
                days.Count);
        }

        // Total Follower: latest snapshot INSIDE the period and never after EndDate.
        var snapshot = DailyReportCalculator.FollowerSnapshotAtOrBefore(
            result.Dataset.FollowerSnapshots
                .Where(s => s.AsOfDate.Date >= range.Start)
                .ToList(),
            range.End);

        var totalFollower = snapshot is null
            ? new PeriodValue(
                null,
                DailyMetricStatus.NoData,
                "Tidak ada snapshot follower yang valid pada periode ini (snapshot setelah EndDate tidak pernah digunakan).",
                0,
                days.Count)
            : new PeriodValue(snapshot.FollowerCount, DailyMetricStatus.Complete, null, days.Count, days.Count);

        return new PeriodContext
        {
            Range = range,
            DaysInPeriod = days.Count,
            SourceAvailable = true,
            SourceUnavailableReason = sourceReason,
            SourceName = result.Dataset.SourceName,
            RecordsByDate = records,
            Reach = reachAgg,
            VideoViews = DailyReportCalculator.Sum(videoViews, days.Count),
            ProfileViews = DailyReportCalculator.Sum(profileViews, days.Count),
            Likes = DailyReportCalculator.Sum(likes, days.Count),
            Comments = DailyReportCalculator.Sum(comments, days.Count),
            Shares = DailyReportCalculator.Sum(shares, days.Count),
            Interactions = interactionsAgg,
            NetFollower = DailyReportCalculator.Sum(netFollower, days.Count),
            EngagementRate = engagementRate,
            TotalFollower = totalFollower,
            ViewerComposition = result.Dataset.ViewerComposition
        };
    }

    private static PeriodValue ToValue(DailyMetricAggregate aggregate, int daysInPeriod, string sourceReason)
        => aggregate.Status switch
        {
            DailyMetricStatus.Unavailable => new PeriodValue(null, DailyMetricStatus.Unavailable, sourceReason, 0, daysInPeriod),
            DailyMetricStatus.NoData => new PeriodValue(null, DailyMetricStatus.NoData, NoDataReason, 0, daysInPeriod),
            _ => new PeriodValue(aggregate.Value, aggregate.Status, null, aggregate.DaysCovered, daysInPeriod)
        };

    // ------------------------------------------------------------------
    // Cards + comparison rows
    // ------------------------------------------------------------------

    private static IReadOnlyList<DailyReportMetric> BuildCards(PeriodContext current, PeriodContext previous)
    {
        var sourceReason = current.SourceUnavailableReason;

        PeriodValue Cur(DailyMetricAggregate agg) => ToValue(agg, current.DaysInPeriod, sourceReason);
        PeriodValue Prev(DailyMetricAggregate agg) => ToValue(agg, previous.DaysInPeriod, previous.SourceUnavailableReason);

        var cards = new List<DailyReportMetric>
        {
            Build(DailyReportMetrics.Reach, "Reach", Cur(current.Reach), Prev(previous.Reach), DailyMetricDeltaKind.Absolute),
            Build(DailyReportMetrics.VideoViews, "Video Views", Cur(current.VideoViews), Prev(previous.VideoViews), DailyMetricDeltaKind.Absolute),
            Build(DailyReportMetrics.ProfileViews, "Profile Views", Cur(current.ProfileViews), Prev(previous.ProfileViews), DailyMetricDeltaKind.Absolute),
            Build(DailyReportMetrics.Interactions, "Interactions", Cur(current.Interactions), Prev(previous.Interactions), DailyMetricDeltaKind.Absolute),
            Build(DailyReportMetrics.NetFollower, "Net Follower", Cur(current.NetFollower), Prev(previous.NetFollower), DailyMetricDeltaKind.Absolute)
        };

        // Engagement Rate: difference in percentage POINTS, never a relative growth %.
        cards.Insert(4, Build(
            DailyReportMetrics.EngagementRate,
            "Engagement Rate",
            current.EngagementRate!,
            previous.EngagementRate!,
            DailyMetricDeltaKind.PercentagePoints));

        // Total Follower: end-of-period position only - no previous value, no growth.
        cards.Add(Build(
            DailyReportMetrics.TotalFollower,
            "Total Follower",
            current.TotalFollower!,
            new PeriodValue(null, DailyMetricStatus.NoData, null, 0, previous.DaysInPeriod),
            DailyMetricDeltaKind.PositionOnly,
            showInComparison: false));

        return cards;
    }

    private static DailyReportMetric Build(
        string key,
        string label,
        PeriodValue current,
        PeriodValue previous,
        DailyMetricDeltaKind deltaKind,
        bool showInComparison = true)
    {
        decimal? difference = null;
        decimal? growth = null;

        if (current.Value.HasValue && previous.Value.HasValue)
        {
            difference = DailyReportCalculator.Difference(current.Value, previous.Value);
            if (deltaKind == DailyMetricDeltaKind.Absolute)
            {
                growth = DailyReportCalculator.GrowthPercentage(current.Value, previous.Value);
            }
        }

        // The card reason always describes the CURRENT value; a missing previous
        // value only suppresses the delta and must not label the current number as
        // unavailable.
        var reason = current.Reason ?? (current.Value is null ? NoDataReason : null);

        return new DailyReportMetric
        {
            Key = key,
            Label = label,
            CurrentValue = current.Value,
            PreviousValue = previous.Value,
            Difference = difference,
            GrowthPercentage = growth,
            DeltaKind = deltaKind,
            CurrentStatus = current.Status,
            PreviousStatus = previous.Status,
            UnavailableReason = reason,
            CoverageNote = current.CoverageNote,
            ShowInComparison = showInComparison
        };
    }

    // ------------------------------------------------------------------
    // Daily rows (table + charts)
    // ------------------------------------------------------------------

    private static IReadOnlyList<DailyReportRow> BuildRows(PeriodContext context)
    {
        var rows = new List<DailyReportRow>(context.DaysInPeriod);

        foreach (var day in context.Range.Days())
        {
            AccountDailyMetricRecord? record = null;
            if (context.SourceAvailable)
            {
                context.RecordsByDate.TryGetValue(day, out record);
            }

            var reach = record?.Reach;
            var interactions = record is null
                ? null
                : DailyReportCalculator.Interactions(record.Likes, record.Comments, record.Shares);

            rows.Add(new DailyReportRow
            {
                Date = day,
                Reach = reach,
                VideoViews = record?.VideoViews,
                ProfileViews = record?.ProfileViews,
                Interactions = interactions,
                EngagementRate = DailyReportCalculator.EngagementRate(interactions, reach),
                NetFollower = record is null
                    ? null
                    : DailyReportCalculator.NetFollower(record.FollowersGained, record.FollowersLost),
                HasRecord = record is not null
            });
        }

        return rows;
    }

    // ------------------------------------------------------------------
    // Donut compositions
    // ------------------------------------------------------------------

    private static DailyReportComposition BuildInteractionComposition(PeriodContext context)
    {
        const string key = "interaction_composition";
        const string title = "Komposisi Interactions";

        if (!context.SourceAvailable)
        {
            return Unavailable(key, title, context.SourceUnavailableReason, InteractionSegments(context, total: null));
        }

        var total = context.Interactions.Value;
        var statuses = new[] { context.Likes.Status, context.Comments.Status, context.Shares.Status, context.Interactions.Status };

        if (!DailyReportCalculator.CanRenderComposition(statuses, total))
        {
            var partial = statuses.Any(s => s == DailyMetricStatus.Partial);
            return Unavailable(key, title, partial
                ? $"Komponen interaksi tidak lengkap pada periode ini ({context.Interactions.DaysCovered} dari {context.DaysInPeriod} hari) - komposisi tidak ditampilkan agar tidak menyesatkan."
                : NoDataReason, InteractionSegments(context, total: null));
        }

        return new DailyReportComposition
        {
            Key = key,
            Title = title,
            IsAvailable = true,
            Total = total,
            Segments = InteractionSegments(context, total)
        };
    }

    /// <summary>
    /// Locked slot order (Like, Comment, Share). Values come from the period
    /// aggregates; pass <c>total: null</c> when the composition must not be shown so
    /// no percentage can ever be derived from an incomplete denominator.
    /// </summary>
    private static IReadOnlyList<DailyReportCompositionSegment> InteractionSegments(PeriodContext context, decimal? total) => new[]
    {
        Segment("like", "Like", "#4d9fff", context.Likes.Value, total),
        Segment("comment", "Comment", "#e8b83f", context.Comments.Value, total),
        Segment("share", "Share", "#a975e8", context.Shares.Value, total)
    };

    private static DailyReportComposition BuildViewerComposition(PeriodContext context)
    {
        const string key = "viewer_composition";
        const string title = "Komposisi Reach · Viewer Baru vs Lama";

        if (!context.SourceAvailable)
        {
            return Unavailable(key, title, context.SourceUnavailableReason, ViewerSegments(null, null));
        }

        var viewers = context.ViewerComposition;
        if (viewers is null || viewers.NewViewers is null || viewers.ReturningViewers is null)
        {
            return Unavailable(key, title,
                "Komposisi viewer baru/lama level akun belum tersedia dari sumber yang terverifikasi.",
                ViewerSegments(null, null));
            // ViewerSegments(null, null): slots stay, percentages stay null.
        }

        // Period-level unique counts come from the source itself; they are never the
        // result of summing daily unique viewers (double counting risk).
        var total = viewers.NewViewers.Value + viewers.ReturningViewers.Value;
        if (total <= 0)
        {
            return Unavailable(key, title, "Total viewer periode ini 0 atau tidak tersedia.", ViewerSegments(null, null));
        }

        return new DailyReportComposition
        {
            Key = key,
            Title = title,
            IsAvailable = true,
            Total = total,
            Segments = ViewerSegments(viewers.NewViewers.Value, viewers.ReturningViewers.Value)
        };
    }

    /// <summary>
    /// Locked slot order (Viewer Baru, Viewer Lama). The denominator is the period's
    /// unique-viewer total supplied by the source (never a sum of daily unique values).
    /// Null inputs render the empty state with no percentage.
    /// </summary>
    private static IReadOnlyList<DailyReportCompositionSegment> ViewerSegments(long? newViewers, long? returningViewers)
    {
        decimal? total = newViewers is null || returningViewers is null
            ? null
            : newViewers.Value + returningViewers.Value;

        return new[]
        {
            Segment("new", "Viewer Baru", "#4ade80", newViewers, total),
            Segment("returning", "Viewer Lama", "#7c8cff", returningViewers, total)
        };
    }

    private static DailyReportCompositionSegment Segment(string key, string label, string color, long? value, decimal? total)
        => new()
        {
            Key = key,
            Label = label,
            Color = color,
            Value = value,
            Percentage = DailyReportCalculator.CompositionPercentage(value, total)
        };

    // Keeps the donut card's shape (same slots + legend) while the values stay null,
    // so the UI renders an informative empty state instead of fake shares.
    private static DailyReportComposition Unavailable(
        string key,
        string title,
        string reason,
        IReadOnlyList<DailyReportCompositionSegment> segments)
        => new()
        {
            Key = key,
            Title = title,
            IsAvailable = false,
            UnavailableReason = reason,
            Segments = segments
        };

    // ------------------------------------------------------------------
    // Notices + footer notes
    // ------------------------------------------------------------------

    private static IReadOnlyList<string> BuildNotices(
        AccountDailyMetricResult currentResult,
        AccountDailyMetricResult previousResult,
        IReadOnlyList<DailyReportMetric> cards)
    {
        var notices = new List<string>();

        if (!currentResult.IsAvailable)
        {
            notices.Add("Seluruh metrik ditampilkan \"-\" karena sumber analytics level akun (harian) belum terintegrasi.");
        }
        else if (!previousResult.IsAvailable)
        {
            notices.Add("Periode pembanding tidak tersedia sehingga kolom Periode Sebelumnya dan Selisih ditampilkan \"-\".");
        }

        var partial = cards
            .Where(c => c.CurrentStatus == DailyMetricStatus.Partial)
            .Select(c => c.Label)
            .ToList();
        if (partial.Count > 0)
        {
            notices.Add($"Data parsial pada {string.Join(", ", partial)} - angka hanya mencakup hari yang memiliki record dan tidak mewakili seluruh periode.");
        }

        if (currentResult.IsAvailable && cards.All(c => c.CurrentValue is null))
        {
            notices.Add("Tidak ada data tercatat untuk periode yang dipilih.");
        }

        return notices;
    }

    private static IReadOnlyList<DailyReportSourceNote> BuildSourceNotes(AccountDailyMetricResult currentResult)
    {
        var accountStatus = currentResult.IsAvailable
            ? $"tersedia dari {currentResult.Dataset?.SourceName ?? "sumber account-level"}"
            : "belum tersambung (blocked)";

        return new List<DailyReportSourceNote>
        {
            new()
            {
                Metric = "Reach",
                Definition = "Agregasi harian Reach / Total Viewers level akun (bukan jumlah Reach per-video).",
                Status = accountStatus
            },
            new()
            {
                Metric = "Video Views",
                Definition = "Agregasi harian Video Views level akun.",
                Status = accountStatus
            },
            new()
            {
                Metric = "Profile Views",
                Definition = "Agregasi harian Profile Views level akun.",
                Status = accountStatus
            },
            new()
            {
                Metric = "Interactions",
                Definition = "Likes + Comments + Shares, dihitung sistem (field gabungan tidak disediakan API).",
                Status = accountStatus
            },
            new()
            {
                Metric = "Engagement Rate",
                Definition = "Interactions / Reach x 100% - Reach sebagai pembagi (Winning Content tetap memakai Views).",
                Status = "dihitung oleh dashboard"
            },
            new()
            {
                Metric = "Net Follower",
                Definition = "Followers Gained - Followers Lost (harian, kemudian diakumulasi per periode).",
                Status = accountStatus
            },
            new()
            {
                Metric = "Total Follower",
                Definition = "Follower count pada posisi akhir Current Period dari snapshot historis valid - tidak pernah memakai data setelah EndDate.",
                Status = accountStatus
            },
            new()
            {
                Metric = "Periode Sebelumnya",
                Definition = "Rentang sama panjang yang berakhir tepat sehari sebelum StartDate (apples-to-apples, tidak overlap).",
                Status = "dihitung oleh dashboard"
            },
            new()
            {
                Metric = "Timezone",
                Definition = "Semua tanggal memakai kalender Asia/Jakarta (GMT+7) dan tidak digeser otomatis.",
                Status = "dihitung oleh dashboard"
            }
        };
    }
}
