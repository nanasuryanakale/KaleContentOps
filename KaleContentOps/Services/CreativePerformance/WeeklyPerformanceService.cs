using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KaleContentOps.Services.CreativePerformance;

// ============================================================================
// Creative Performance - Weekly Report service.
//
// Design (approved Weekly Report spec):
//  - Monday-Sunday buckets, CLIPPED to the selected range (never snapped):
//    see WeeklyReportBucketing.
//  - Equal-duration Previous Period, same convention as the Daily Report
//    (DailyReportDateRange.Previous): 23/08-05/09 -> 09/08-22/08.
//  - ONE provider query per clipped bucket window, SEQUENTIALLY:
//      * every window stays inside its bucket's actual date range and is at
//        most 7 calendar days - no full-period or unbounded fan-out requests;
//      * the provider's cache, rate limiting and error handling are reused as
//       -is (nothing provider-wide is touched);
//      * a provider that cannot answer the FIRST window of a period is not
//        asked again for that period's remaining windows (mirrors the Daily
//        Report's "no point asking twice" rule); the skipped buckets stay
//        Unavailable with the real reason - never zeroes.
//  - A windowed PeriodInteractions total returned for a bucket represents
//    EXACTLY that bucket's window (the provider aggregates over the requested
//    half-open window) and is preferred over the daily sums - it is never
//    distributed across days or other weeks and never added to daily values.
//  - Period values = sum of the bucket values. Missing buckets are NOT zero:
//    the period is Partial (with a coverage note) until every bucket carries
//    a value, and rows / charts / cards all read the same bucket results.
//  - Verified metrics today: Video Views and Interactions. Reach, Profile
//    Views, Engagement Rate (needs its Reach denominator), Net Follower and
//    Total Follower stay Unavailable ("-") with honest reasons until a
//    verified account-level source exists - no per-video sums, no fake zeroes,
//    no new migrations.
// ============================================================================

public sealed class WeeklyPerformanceService : IWeeklyPerformanceService
{
    private const string NoDataPeriodReason = "Belum ada data tercatat pada periode ini.";
    private const string NoDataWeekReason = "Belum ada data tercatat pada minggu ini.";

    private const string ErRequiresReachReason =
        "Engagement Rate membutuhkan Reach (formula Interactions / Reach x 100%) dan Reach belum memiliki " +
        "sumber data terverifikasi - nilai tetap ditampilkan \"-\" (bukan diganti dengan Views).";

    private const string ViewerMultiWindowReason =
        "Komposisi viewer baru/lama hanya dapat ditampilkan untuk rentang yang dicakup satu jendela minggu - " +
        "jumlah viewer unik tidak pernah dijumlah lintas minggu (risiko double counting) dan sumber belum " +
        "menyediakannya.";

    private readonly IAccountDailyMetricSource _source;
    private readonly IShopTimeZone _shopTimeZone;
    private readonly ILogger<WeeklyPerformanceService> _logger;

    public WeeklyPerformanceService(
        IAccountDailyMetricSource source,
        IShopTimeZone shopTimeZone,
        ILogger<WeeklyPerformanceService>? logger = null)
    {
        _source = source;
        _shopTimeZone = shopTimeZone;
        _logger = logger ?? NullLogger<WeeklyPerformanceService>.Instance;
    }

    public async Task<WeeklyReportData> BuildAsync(WeeklyReportFilter filter, CancellationToken cancellationToken = default)
    {
        // Validated inclusive range; EndDate < StartDate throws (the controller
        // turns that into a 400). The user's dates are never shifted or snapped.
        var current = DailyReportDateRange.Create(filter.StartDate, filter.EndDate, "StartDate", "EndDate");
        var previous = current.Previous();

        var currentBuckets = WeeklyReportBucketing.Buckets(current);
        var previousBuckets = WeeklyReportBucketing.Buckets(previous);

        var currentPeriod = await BuildPeriodAsync(current, currentBuckets, cancellationToken).ConfigureAwait(false);

        // No point asking the provider twice when it cannot answer at all
        // (same rule as the Daily Report).
        var previousPeriod = currentPeriod.SourceAvailable
            ? await BuildPeriodAsync(previous, previousBuckets, cancellationToken).ConfigureAwait(false)
            : BuildPeriodContext(previous, previousBuckets
                .Select(bucket => UnavailableBucket(bucket, currentPeriod.SourceUnavailableReason))
                .ToList());

        var cards = BuildCards(currentPeriod, previousPeriod);
        var trendRows = BuildRows(currentPeriod);

        var interactionComposition = BuildInteractionComposition(currentPeriod);
        var viewerComposition = BuildViewerComposition(currentPeriod);

        var notices = BuildNotices(currentPeriod, previousPeriod, cards);

        // Honest delta-suppression explanation (same convention as the Daily
        // Report): when the CURRENT period carries Interactions but the
        // PREVIOUS one does not, the comparison columns render "-" and the
        // operator is told why. A current value is NEVER copied into the
        // previous column and a missing previous is NEVER treated as zero.
        var interactionsCard = cards.Single(c => c.Key == DailyReportMetrics.Interactions);
        if (interactionsCard.CurrentValue.HasValue && !interactionsCard.PreviousValue.HasValue)
        {
            string? previousReason;
            if (!previousPeriod.SourceAvailable)
            {
                previousReason = previousPeriod.SourceUnavailableReason;
            }
            else
            {
                previousReason = previousPeriod.Interactions.Status switch
                {
                    DailyMetricStatus.Unavailable => previousPeriod.LimitedSourceReason ?? previousPeriod.SourceUnavailableReason,
                    DailyMetricStatus.NoData => "belum ada data tercatat pada periode pembanding",
                    _ => null
                };
            }

            if (!string.IsNullOrWhiteSpace(previousReason))
            {
                notices = notices.Append(
                    "Interactions periode sebelumnya tidak tersedia (" + previousReason + ") sehingga kolom " +
                    "Periode Sebelumnya, Selisih, dan pertumbuhan Interactions ditampilkan \"-\" - nilai periode " +
                    "aktif tidak pernah dipakai sebagai pengganti dan nilai yang hilang tidak dianggap nol.").ToList();
            }
        }

        // Safe per-build diagnostics (no tokens/secrets): period, bucket count,
        // availability and the two verified metric states - enough to locate a
        // suppression without a debugger.
        var videoViewsCard = cards.Single(c => c.Key == DailyReportMetrics.VideoViews);
        _logger.LogInformation(
            "Weekly Report built {Start}..{End} into {BucketCount} buckets (previous {PreviousStart}..{PreviousEnd}, {PreviousBucketCount} buckets): " +
            "sourceAvailable={SourceAvailable}, VideoViews={Value} ({Status}), previous VideoViews={PreviousValue} ({PreviousStatus})",
            current.Start.ToString("yyyy-MM-dd"),
            current.End.ToString("yyyy-MM-dd"),
            currentBuckets.Count,
            previous.Start.ToString("yyyy-MM-dd"),
            previous.End.ToString("yyyy-MM-dd"),
            previousBuckets.Count,
            currentPeriod.SourceAvailable,
            videoViewsCard.CurrentValue,
            videoViewsCard.CurrentStatus,
            videoViewsCard.PreviousValue,
            videoViewsCard.PreviousStatus);

        return new WeeklyReportData
        {
            CurrentStart = current.Start,
            CurrentEnd = current.End,
            PreviousStart = previous.Start,
            PreviousEnd = previous.End,
            CurrentDayCount = current.DayCount,
            PreviousDayCount = previous.DayCount,
            CurrentBucketCount = currentBuckets.Count,
            PreviousBucketCount = previousBuckets.Count,
            TimeZoneId = _shopTimeZone.TimeZoneId,
            TimeZoneLabel = _shopTimeZone.TimeZoneId == ShopTimeZoneOptions.DefaultTimeZoneId
                ? "GMT+7"
                : _shopTimeZone.TimeZoneId,
            Cards = cards,
            ComparisonRows = cards.Where(c => c.ShowInComparison).ToList(),
            // Same row objects, opposite order: charts read ascending, the table descending.
            Trend = trendRows,
            WeeklyRows = trendRows.AsEnumerable().Reverse().ToList(),
            InteractionComposition = interactionComposition,
            ViewerComposition = viewerComposition,
            Notices = notices,
            SourceNotes = BuildSourceNotes(currentPeriod),
            HasAnyMetricData = cards.Any(c => c.CurrentValue.HasValue)
        };
    }

    // ------------------------------------------------------------------
    // Metric keys of the summary cards, with their display labels (mockup order).
    // ------------------------------------------------------------------

    private static readonly (string Key, string Label)[] CardMetrics =
    {
        (DailyReportMetrics.Reach, "Reach"),
        (DailyReportMetrics.VideoViews, "Video Views"),
        (DailyReportMetrics.ProfileViews, "Profile Views"),
        (DailyReportMetrics.Interactions, "Interactions"),
        (DailyReportMetrics.EngagementRate, "Engagement Rate"),
        (DailyReportMetrics.NetFollower, "Net Follower"),
        (DailyReportMetrics.TotalFollower, "Total Follower")
    };

    /// <summary>
    /// Reason used for metrics OUTSIDE a verified-partial dataset's declared set.
    /// Built from the seven dashboard summary keys only (Weekly has no shop-video section).
    /// </summary>
    private static string LimitedReasonFor(IReadOnlySet<string> availableKeys)
    {
        var availableLabels = CardMetrics
            .Where(m => availableKeys.Contains(m.Key))
            .Select(m => m.Label)
            .ToList();

        return availableLabels.Count > 0
            ? "Metrik ini belum memiliki sumber data terverifikasi dan ditampilkan \"-\". " +
              $"Sumber saat ini hanya menyediakan: {string.Join(", ", availableLabels)}."
            : "Metrik ini belum memiliki sumber data terverifikasi dan ditampilkan \"-\". " +
              "Sumber saat ini tidak menyediakan metrik ringkasan akun yang terverifikasi.";
    }

    // ------------------------------------------------------------------
    // Period evaluation
    // ------------------------------------------------------------------

    /// <summary>One clipped weekly bucket, evaluated once against the provider.</summary>
    private sealed class BucketContext
    {
        public required WeeklyBucket Bucket { get; init; }

        public bool SourceAvailable { get; init; }
        public string SourceUnavailableReason { get; init; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;

        /// <summary>Declared metric keys of the dataset (null = every metric supplied).</summary>
        public IReadOnlySet<string>? AvailableMetricKeys { get; init; }

        /// <summary>Reason used for metrics OUTSIDE the declared set (null when the dataset is complete).</summary>
        public string? LimitedSourceReason { get; init; }

        /// <summary>True when the source actually supplies this metric key (declared or complete dataset).</summary>
        public bool Supplies(string key) => AvailableMetricKeys is null || AvailableMetricKeys.Contains(key);

        public DailyMetricAggregate Reach { get; init; }
        public DailyMetricAggregate VideoViews { get; init; }
        public DailyMetricAggregate ProfileViews { get; init; }
        public DailyMetricAggregate Likes { get; init; }
        public DailyMetricAggregate Comments { get; init; }
        public DailyMetricAggregate Shares { get; init; }
        public DailyMetricAggregate Interactions { get; init; }
        public DailyMetricAggregate NetFollower { get; init; }

        /// <summary>Weekly ER of this bucket (percent points). Null while Reach is unavailable/zero.</summary>
        public decimal? EngagementRate { get; init; }

        /// <summary>Latest valid follower snapshot inside this bucket (never after its end).</summary>
        public FollowerSnapshot? FollowerSnapshot { get; init; }

        /// <summary>Period-level viewer split supplied by the source for this bucket's window.</summary>
        public AccountViewerComposition? ViewerComposition { get; init; }
    }

    /// <summary>All aggregates of one period, evaluated once and reused everywhere.</summary>
    private sealed class PeriodContext
    {
        public DailyReportDateRange Range { get; init; }
        public IReadOnlyList<BucketContext> Buckets { get; init; } = Array.Empty<BucketContext>();
        public bool SourceAvailable { get; init; }
        public string SourceUnavailableReason { get; init; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;

        public IReadOnlySet<string>? AvailableMetricKeys { get; init; }
        public string? LimitedSourceReason { get; init; }

        public bool Supplies(string key) => AvailableMetricKeys is null || AvailableMetricKeys.Contains(key);

        public int BucketsInPeriod => Buckets.Count;

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

        /// <summary>
        /// Period-level viewer split, only when the whole period is ONE queried
        /// window (a single bucket). Unique-viewer counts are never summed across
        /// bucket windows (double counting).
        /// </summary>
        public AccountViewerComposition? ViewerComposition { get; init; }
    }

    /// <summary>One period's value for one metric, with completeness and a display-ready reason.</summary>
    private sealed record PeriodValue(
        decimal? Value,
        DailyMetricStatus Status,
        string? Reason,
        int BucketsCovered,
        int BucketsInPeriod)
    {
        public string? CoverageNote => Status switch
        {
            DailyMetricStatus.Partial when BucketsCovered < BucketsInPeriod
                => $"Data parsial: {BucketsCovered} dari {BucketsInPeriod} minggu tercatat.",
            DailyMetricStatus.Partial
                => "Data parsial: seluruh minggu tercatat namun sebagian hari di dalamnya tidak tercatat.",
            _ => null
        };
    }

    private static BucketContext UnavailableBucket(WeeklyBucket bucket, string reason)
        => new()
        {
            Bucket = bucket,
            SourceAvailable = false,
            SourceUnavailableReason = reason,
            Reach = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            VideoViews = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            ProfileViews = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Likes = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Comments = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Shares = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Interactions = DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            NetFollower = DailyMetricAggregate.None(DailyMetricStatus.Unavailable)
        };

    private async Task<PeriodContext> BuildPeriodAsync(
        DailyReportDateRange range,
        IReadOnlyList<WeeklyBucket> buckets,
        CancellationToken cancellationToken)
    {
        var bucketContexts = new List<BucketContext>(buckets.Count);

        // SEQUENTIAL provider reads: one window per clipped bucket (<= 7 days),
        // reusing the provider's own cache, backoff and rate-limit protections.
        // No parallel fan-out is created here.
        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];

            // A provider that cannot answer the FIRST window of the period is not
            // asked again for its remaining windows (Daily Report convention).
            // The skipped buckets stay Unavailable with the real reason.
            if (i > 0 && bucketContexts[0].SourceAvailable == false)
            {
                bucketContexts.Add(UnavailableBucket(bucket, bucketContexts[0].SourceUnavailableReason));
                continue;
            }

            var result = await _source.GetAsync(
                new AccountDailyMetricQuery { Start = bucket.Start, EndExclusive = bucket.EndExclusive },
                cancellationToken).ConfigureAwait(false);

            if (!result.IsAvailable || result.Dataset is null)
            {
                var reason = string.IsNullOrWhiteSpace(result.UnavailableReason)
                    ? UnavailableAccountDailyMetricSource.Reason
                    : result.UnavailableReason!;
                bucketContexts.Add(UnavailableBucket(bucket, reason));
                continue;
            }

            bucketContexts.Add(BuildBucketContext(bucket, result.Dataset));
        }

        return BuildPeriodContext(range, bucketContexts);
    }

    private static BucketContext BuildBucketContext(WeeklyBucket bucket, AccountDailyMetricDataset dataset)
    {
        var daysInBucket = bucket.DayCount;
        var availableKeys = dataset.AvailableMetricKeys;
        var limitedReason = availableKeys is null ? null : LimitedReasonFor(availableKeys);

        // Records clipped to this bucket's window: a record outside it is never
        // counted, and days without a record stay null (never 0).
        var records = new Dictionary<DateTime, AccountDailyMetricRecord>();
        foreach (var record in dataset.Records)
        {
            var date = record.Date.Date;
            if (date < bucket.Start || date > bucket.End || records.ContainsKey(date))
            {
                continue;
            }

            records[date] = record;
        }

        var reachDays = new List<long?>(daysInBucket);
        var videoViewsDays = new List<long?>(daysInBucket);
        var profileViewsDays = new List<long?>(daysInBucket);
        var likesDays = new List<long?>(daysInBucket);
        var commentsDays = new List<long?>(daysInBucket);
        var sharesDays = new List<long?>(daysInBucket);
        var interactionsDays = new List<long?>(daysInBucket);
        var netFollowerDays = new List<long?>(daysInBucket);

        for (var day = bucket.Start; day <= bucket.End; day = day.AddDays(1))
        {
            records.TryGetValue(day, out var record);

            reachDays.Add(record?.Reach);
            videoViewsDays.Add(record?.VideoViews);
            profileViewsDays.Add(record?.ProfileViews);
            likesDays.Add(record?.Likes);
            commentsDays.Add(record?.Comments);
            sharesDays.Add(record?.Shares);
            // Composite metric: a record missing any component yields null, never a partial total.
            interactionsDays.Add(record is null
                ? null
                : DailyReportCalculator.Interactions(record.Likes, record.Comments, record.Shares));
            netFollowerDays.Add(record is null
                ? null
                : DailyReportCalculator.NetFollower(record.FollowersGained, record.FollowersLost));
        }

        var reachAgg = dataset.AvailableMetricKeys is null || availableKeys!.Contains(DailyReportMetrics.Reach)
            ? DailyReportCalculator.Sum(reachDays, daysInBucket)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var videoViewsAgg = dataset.AvailableMetricKeys is null || availableKeys!.Contains(DailyReportMetrics.VideoViews)
            ? DailyReportCalculator.Sum(videoViewsDays, daysInBucket)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var profileViewsAgg = dataset.AvailableMetricKeys is null || availableKeys!.Contains(DailyReportMetrics.ProfileViews)
            ? DailyReportCalculator.Sum(profileViewsDays, daysInBucket)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var netFollowerAgg = dataset.AvailableMetricKeys is null || availableKeys!.Contains(DailyReportMetrics.NetFollower)
            ? DailyReportCalculator.Sum(netFollowerDays, daysInBucket)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);

        // Interactions: the EXACT windowed totals the source supplies for THIS
        // bucket's window win over the daily sums (the windowed aggregate covers
        // the whole window by construction). It is never added to the daily sums
        // (no double counting) and never redistributed to days or other weeks.
        var windowed = dataset.PeriodInteractions;
        var hasWindowedInteractions = windowed is not null
            && windowed.Likes.HasValue
            && windowed.Comments.HasValue
            && windowed.Shares.HasValue;

        DailyMetricAggregate likesAgg;
        DailyMetricAggregate commentsAgg;
        DailyMetricAggregate sharesAgg;
        DailyMetricAggregate interactionsAgg;

        if (dataset.AvailableMetricKeys is not null && !availableKeys!.Contains(DailyReportMetrics.Interactions))
        {
            likesAgg = commentsAgg = sharesAgg = interactionsAgg =
                DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        }
        else if (hasWindowedInteractions)
        {
            likesAgg = new DailyMetricAggregate(windowed!.Likes, daysInBucket, DailyMetricStatus.Complete);
            commentsAgg = new DailyMetricAggregate(windowed.Comments, daysInBucket, DailyMetricStatus.Complete);
            sharesAgg = new DailyMetricAggregate(windowed.Shares, daysInBucket, DailyMetricStatus.Complete);
            interactionsAgg = new DailyMetricAggregate(
                windowed.Likes!.Value + windowed.Comments!.Value + windowed.Shares!.Value,
                daysInBucket,
                DailyMetricStatus.Complete);
        }
        else
        {
            likesAgg = DailyReportCalculator.Sum(likesDays, daysInBucket);
            commentsAgg = DailyReportCalculator.Sum(commentsDays, daysInBucket);
            sharesAgg = DailyReportCalculator.Sum(sharesDays, daysInBucket);
            interactionsAgg = DailyReportCalculator.Sum(interactionsDays, daysInBucket);
        }

        // Weekly ER of the bucket = its Interactions / its Reach (null while the
        // Reach denominator is unavailable - never swapped for Views).
        var engagementRate = DailyReportCalculator.EngagementRate(interactionsAgg.Value, reachAgg.Value);

        // Total Follower: latest snapshot INSIDE the bucket and never after its end.
        var snapshot = dataset.AvailableMetricKeys is null || availableKeys!.Contains(DailyReportMetrics.TotalFollower)
            ? DailyReportCalculator.FollowerSnapshotAtOrBefore(
                dataset.FollowerSnapshots.Where(s => s.AsOfDate.Date >= bucket.Start).ToList(),
                bucket.End)
            : null;

        return new BucketContext
        {
            Bucket = bucket,
            SourceAvailable = true,
            SourceName = dataset.SourceName,
            AvailableMetricKeys = availableKeys,
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

    private static PeriodContext BuildPeriodContext(DailyReportDateRange range, IReadOnlyList<BucketContext> buckets)
    {
        var availableBuckets = buckets.Where(b => b.SourceAvailable).ToList();
        var sourceAvailable = availableBuckets.Count > 0;

        var sourceUnavailableReason = sourceAvailable
            ? string.Empty
            : buckets.Select(b => b.SourceUnavailableReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
              ?? UnavailableAccountDailyMetricSource.Reason;

        // Declared metric scope / provider metadata from the first available
        // bucket (one provider, one contract - the declared set is constant).
        var first = availableBuckets.FirstOrDefault();
        var availableKeys = first?.AvailableMetricKeys;
        var limitedReason = first?.LimitedSourceReason;
        var sourceName = first?.SourceName ?? string.Empty;

        // ---- Additive period aggregates: sum of bucket values. A bucket without
        // a value is skipped (never counted as zero), so a period with missing or
        // unavailable buckets is honestly Partial / NoData / Unavailable instead
        // of a deflated "complete" sum. ----
        DailyMetricAggregate AggregateBuckets(Func<BucketContext, DailyMetricAggregate> selector)
        {
            long? sum = null;
            var covered = 0;
            var complete = 0;
            var anyUnavailable = false;

            foreach (var bucket in buckets)
            {
                var aggregate = selector(bucket);
                if (aggregate.Status == DailyMetricStatus.Unavailable)
                {
                    anyUnavailable = true;
                    continue;
                }

                if (aggregate.Value is null)
                {
                    continue; // NoData bucket contributes nothing - a missing observation is never zero.
                }

                sum = (sum ?? 0L) + aggregate.Value.Value;
                covered++;
                if (aggregate.Status == DailyMetricStatus.Complete)
                {
                    complete++;
                }
            }

            if (covered == 0)
            {
                return DailyMetricAggregate.None(
                    anyUnavailable ? DailyMetricStatus.Unavailable : DailyMetricStatus.NoData);
            }

            var status = complete == buckets.Count ? DailyMetricStatus.Complete : DailyMetricStatus.Partial;
            return new DailyMetricAggregate(sum, covered, status);
        }

        var reachAgg = AggregateBuckets(b => b.Reach);
        var videoViewsAgg = AggregateBuckets(b => b.VideoViews);
        var profileViewsAgg = AggregateBuckets(b => b.ProfileViews);
        var likesAgg = AggregateBuckets(b => b.Likes);
        var commentsAgg = AggregateBuckets(b => b.Comments);
        var sharesAgg = AggregateBuckets(b => b.Shares);
        var interactionsAgg = AggregateBuckets(b => b.Interactions);
        var netFollowerAgg = AggregateBuckets(b => b.NetFollower);

        var bucketsInPeriod = buckets.Count;

        if (!sourceAvailable)
        {
            var reason = sourceUnavailableReason;
            return new PeriodContext
            {
                Range = range,
                Buckets = buckets,
                SourceAvailable = false,
                SourceUnavailableReason = reason,
                SourceName = sourceName,
                AvailableMetricKeys = availableKeys,
                LimitedSourceReason = limitedReason,
                Reach = reachAgg,
                VideoViews = videoViewsAgg,
                ProfileViews = profileViewsAgg,
                Likes = likesAgg,
                Comments = commentsAgg,
                Shares = sharesAgg,
                Interactions = interactionsAgg,
                NetFollower = netFollowerAgg,
                // When the whole source is down every metric explains itself with the
                // real outage reason (same convention as the Daily Report).
                EngagementRate = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, bucketsInPeriod),
                TotalFollower = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, bucketsInPeriod)
            };
        }

        // Period ER = Total Interactions / Total Reach x 100 (never the average of
        // the weekly ERs). Stays null while the Reach denominator is unavailable.
        PeriodValue engagementRate;
        if (interactionsAgg.Value is null || reachAgg.Value is null)
        {
            var status = DailyReportCalculator.Worst(interactionsAgg.Status, reachAgg.Status);
            var erReason = reachAgg.Status == DailyMetricStatus.Unavailable
                ? ErRequiresReachReason
                : status == DailyMetricStatus.Unavailable ? limitedReason ?? sourceUnavailableReason : NoDataPeriodReason;
            engagementRate = new PeriodValue(null, status, erReason, 0, bucketsInPeriod);
        }
        else if (reachAgg.Value <= 0)
        {
            engagementRate = new PeriodValue(
                null,
                DailyMetricStatus.NoData,
                "Engagement Rate tidak dapat dihitung karena Reach periode ini tidak tersedia atau 0.",
                0,
                bucketsInPeriod);
        }
        else
        {
            engagementRate = new PeriodValue(
                DailyReportCalculator.PeriodEngagementRate(interactionsAgg.Value, reachAgg.Value),
                DailyReportCalculator.Worst(interactionsAgg.Status, reachAgg.Status),
                null,
                Math.Min(interactionsAgg.DaysCovered, reachAgg.DaysCovered),
                bucketsInPeriod);
        }

        // Total Follower: latest snapshot INSIDE the period and never after EndDate.
        var snapshot = buckets.Select(b => b.FollowerSnapshot)
            .Where(s => s is not null)
            .OrderByDescending(s => s!.AsOfDate)
            .ThenByDescending(s => s!.CapturedAt)
            .FirstOrDefault();

        var totalFollower = !buckets[0].Supplies(DailyReportMetrics.TotalFollower)
            ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, bucketsInPeriod)
            : snapshot is null
                ? new PeriodValue(
                    null,
                    DailyMetricStatus.NoData,
                    "Tidak ada snapshot follower yang valid pada periode ini (snapshot setelah EndDate tidak pernah digunakan).",
                    0,
                    bucketsInPeriod)
                : new PeriodValue(snapshot.FollowerCount, DailyMetricStatus.Complete, null, bucketsInPeriod, bucketsInPeriod);

        // Period-level viewer split: unique viewers exist only for the exact
        // window they were queried for, so they are only honoured when the whole
        // period IS one queried window (a single bucket). They are never summed
        // across buckets.
        var viewerComposition = bucketsInPeriod == 1 ? first?.ViewerComposition : null;

        return new PeriodContext
        {
            Range = range,
            Buckets = buckets,
            SourceAvailable = true,
            SourceName = sourceName,
            AvailableMetricKeys = availableKeys,
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
            TotalFollower = totalFollower,
            ViewerComposition = viewerComposition
        };
    }

    private static PeriodValue ToValue(DailyMetricAggregate aggregate, int bucketsInPeriod, string sourceReason)
        => aggregate.Status switch
        {
            DailyMetricStatus.Unavailable => new PeriodValue(null, DailyMetricStatus.Unavailable, sourceReason, 0, bucketsInPeriod),
            DailyMetricStatus.NoData => new PeriodValue(null, DailyMetricStatus.NoData, NoDataPeriodReason, 0, bucketsInPeriod),
            _ => new PeriodValue(aggregate.Value, aggregate.Status, null, aggregate.DaysCovered, bucketsInPeriod)
        };

    // ------------------------------------------------------------------
    // Cards + comparison rows
    // ------------------------------------------------------------------

    private static IReadOnlyList<DailyReportMetric> BuildCards(PeriodContext current, PeriodContext previous)
    {
        var sourceReason = current.SourceUnavailableReason;

        // A fully unavailable source explains itself with SourceUnavailableReason;
        // a limited (verified-partial) source explains each unsupplied metric with
        // the LimitedSourceReason instead.
        PeriodValue Cur(DailyMetricAggregate agg) => ToValue(
            agg,
            current.BucketsInPeriod,
            agg.Status == DailyMetricStatus.Unavailable && string.IsNullOrEmpty(sourceReason)
                ? current.LimitedSourceReason ?? string.Empty
                : sourceReason);
        PeriodValue Prev(DailyMetricAggregate agg) => ToValue(
            agg,
            previous.BucketsInPeriod,
            agg.Status == DailyMetricStatus.Unavailable && string.IsNullOrEmpty(previous.SourceUnavailableReason)
                ? previous.LimitedSourceReason ?? string.Empty
                : previous.SourceUnavailableReason);

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
            new PeriodValue(null, DailyMetricStatus.NoData, null, 0, previous.BucketsInPeriod),
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
        var reason = current.Reason ?? (current.Value is null ? NoDataPeriodReason : null);

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
    // Weekly rows (table + charts) - one row per clipped bucket
    // ------------------------------------------------------------------

    private static IReadOnlyList<WeeklyReportRow> BuildRows(PeriodContext context)
    {
        var rows = new List<WeeklyReportRow>(context.Buckets.Count);

        foreach (var bucketContext in context.Buckets)
        {
            var bucket = bucketContext.Bucket;
            var sourceAvailable = bucketContext.SourceAvailable;

            var reach = sourceAvailable ? bucketContext.Reach.Value : null;
            var interactions = sourceAvailable ? bucketContext.Interactions.Value : null;

            var states = new Dictionary<string, WeeklyMetricState>(StringComparer.Ordinal)
            {
                [DailyReportMetrics.Reach] = RowState(bucketContext, bucketContext.Reach, NoDataWeekReason),
                [DailyReportMetrics.VideoViews] = RowState(bucketContext, bucketContext.VideoViews, NoDataWeekReason),
                [DailyReportMetrics.ProfileViews] = RowState(bucketContext, bucketContext.ProfileViews, NoDataWeekReason),
                [DailyReportMetrics.Interactions] = RowState(bucketContext, bucketContext.Interactions, NoDataWeekReason),
                [DailyReportMetrics.NetFollower] = RowState(bucketContext, bucketContext.NetFollower, NoDataWeekReason),
                [DailyReportMetrics.EngagementRate] = EngagementRateRowState(bucketContext)
            };

            rows.Add(new WeeklyReportRow
            {
                Start = bucket.Start,
                End = bucket.End,
                DayCount = bucket.DayCount,
                IsPartial = bucket.IsPartial,
                Reach = reach,
                VideoViews = sourceAvailable ? bucketContext.VideoViews.Value : null,
                ProfileViews = sourceAvailable ? bucketContext.ProfileViews.Value : null,
                Interactions = interactions,
                EngagementRate = sourceAvailable ? bucketContext.EngagementRate : null,
                NetFollower = sourceAvailable ? bucketContext.NetFollower.Value : null,
                MetricStates = states
            });
        }

        return rows;
    }

    private static WeeklyMetricState RowState(BucketContext bucketContext, DailyMetricAggregate aggregate, string noDataReason)
        => new()
        {
            Status = aggregate.Status,
            DaysCovered = aggregate.DaysCovered,
            DaysInBucket = bucketContext.Bucket.DayCount,
            Reason = aggregate.Status switch
            {
                DailyMetricStatus.Unavailable => bucketContext.SourceAvailable
                    ? bucketContext.LimitedSourceReason ?? bucketContext.SourceUnavailableReason
                    : bucketContext.SourceUnavailableReason,
                DailyMetricStatus.NoData => noDataReason,
                _ => null
            }
        };

    private static WeeklyMetricState EngagementRateRowState(BucketContext bucketContext)
    {
        var status = DailyReportCalculator.Worst(bucketContext.Interactions.Status, bucketContext.Reach.Status);
        var reason = bucketContext.Reach.Status == DailyMetricStatus.Unavailable
            ? ErRequiresReachReason
            : status == DailyMetricStatus.Unavailable
                ? (bucketContext.SourceAvailable ? bucketContext.LimitedSourceReason : bucketContext.SourceUnavailableReason)
                : status == DailyMetricStatus.NoData ? NoDataWeekReason : null;

        return new WeeklyMetricState
        {
            Status = status,
            DaysCovered = status is DailyMetricStatus.Complete or DailyMetricStatus.Partial
                ? Math.Min(bucketContext.Interactions.DaysCovered, bucketContext.Reach.DaysCovered)
                : 0,
            DaysInBucket = bucketContext.Bucket.DayCount,
            Reason = reason
        };
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

        // A source that does not declare Interactions must never render "no data
        // recorded" for the composition - it is UNAVAILABLE, and the honest
        // limited-source reason says so.
        if (!context.Supplies(DailyReportMetrics.Interactions))
        {
            return Unavailable(key, title,
                context.LimitedSourceReason ?? context.SourceUnavailableReason,
                InteractionSegments(context, total: null));
        }

        var total = context.Interactions.Value;
        var statuses = new[] { context.Likes.Status, context.Comments.Status, context.Shares.Status, context.Interactions.Status };

        if (!DailyReportCalculator.CanRenderComposition(statuses, total))
        {
            var partial = statuses.Any(s => s == DailyMetricStatus.Partial);
            var unavailable = statuses.Any(s => s == DailyMetricStatus.Unavailable);
            // The partial reason must describe the REAL gap: whole missing weeks are
            // counted in buckets, while a bucket with unrecorded days is described as
            // such (the two counts must never be mixed into one misleading number).
            var partialReason = context.Interactions.DaysCovered < context.BucketsInPeriod
                ? $"Komponen interaksi tidak lengkap pada periode ini ({context.Interactions.DaysCovered} dari {context.BucketsInPeriod} minggu tercatat) - komposisi tidak ditampilkan agar tidak menyesatkan."
                : "Komponen interaksi tidak lengkap pada periode ini (sebagian hari dalam minggu tidak tercatat) - komposisi tidak ditampilkan agar tidak menyesatkan.";
            return Unavailable(key, title, partial
                ? partialReason
                : unavailable
                    ? context.LimitedSourceReason ?? context.SourceUnavailableReason
                    : NoDataPeriodReason, InteractionSegments(context, total: null));
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
    /// aggregates; pass <c>total: null</c> when the composition must not be shown
    /// so no percentage can ever be derived from an incomplete denominator.
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
                context.BucketsInPeriod > 1
                    ? ViewerMultiWindowReason
                    : context.LimitedSourceReason is null
                        ? "Komposisi viewer baru/lama level akun belum tersedia dari sumber yang terverifikasi."
                        : context.LimitedSourceReason,
                ViewerSegments(null, null));
        }

        // Period-level unique counts come from the source itself; they are never
        // the result of summing weekly unique viewers (double counting risk).
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
    /// Locked slot order (Viewer Baru, Viewer Lama). The denominator is the
    /// period's unique-viewer total supplied by the source (never a sum of weekly
    /// unique values). Null inputs render the empty state with no percentage.
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

    // Keeps the donut card's shape (same slots + legend) while the values stay
    // null, so the UI renders an informative empty state instead of fake shares.
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
        PeriodContext current,
        PeriodContext previous,
        IReadOnlyList<DailyReportMetric> cards)
    {
        var notices = new List<string>();

        if (!current.SourceAvailable)
        {
            // Surface the provider's ACTUAL reason (transport failure, HTTP/business
            // error, missing configuration...), never a generic placeholder.
            var reason = string.IsNullOrWhiteSpace(current.SourceUnavailableReason)
                ? UnavailableAccountDailyMetricSource.Reason
                : current.SourceUnavailableReason;
            notices.Add(
                "Seluruh metrik ditampilkan \"-\" karena sumber analytics level akun (harian) belum terintegrasi " +
                "atau belum dapat dihubungi pada permintaan ini. Detail: " + reason);
        }
        else
        {
            if (!previous.SourceAvailable)
            {
                notices.Add("Periode pembanding tidak tersedia sehingga kolom Periode Sebelumnya dan Selisih ditampilkan \"-\".");
            }

            // Verified-partial source: some metric families are still without a
            // verified source and must be called out explicitly instead of
            // silently showing "-".
            var unavailable = cards
                .Where(c => c.CurrentStatus == DailyMetricStatus.Unavailable)
                .Select(c => c.Label)
                .ToList();
            if (unavailable.Count > 0)
            {
                notices.Add($"Metrik {string.Join(", ", unavailable)} belum memiliki sumber data yang terverifikasi dan ditampilkan \"-\".");
            }
        }

        var partial = cards
            .Where(c => c.CurrentStatus == DailyMetricStatus.Partial)
            .Select(c => c.Label)
            .ToList();
        if (partial.Count > 0)
        {
            // Reconciliation rule: the period total is the sum of the weekly
            // buckets that carry a value - a bucket without data is NEVER counted
            // as zero, so the total never claims to represent missing weeks.
            notices.Add(
                $"Data parsial pada {string.Join(", ", partial)} - total hanya menjumlahkan minggu yang memiliki " +
                "data (minggu tanpa data tidak dianggap nol) dan tidak mewakili seluruh periode.");
        }

        if (current.SourceAvailable && cards.All(c => c.CurrentValue is null))
        {
            notices.Add("Tidak ada data tercatat untuk periode yang dipilih.");
        }

        return notices;
    }

    private static IReadOnlyList<DailyReportSourceNote> BuildSourceNotes(PeriodContext current)
    {
        var sourceName = string.IsNullOrWhiteSpace(current.SourceName) ? "sumber account-level" : current.SourceName;
        var availableKeys = current.AvailableMetricKeys;

        // Per-metric footer status: a metric is only "tersedia" when the registered
        // provider explicitly declares it (verified mapping). Everything else is
        // honestly reported as not-yet-verified, even when the source itself answers.
        string StatusFor(string key) => !current.SourceAvailable
            ? "belum tersambung (blocked)"
            : availableKeys is null || availableKeys.Contains(key)
                ? $"tersedia dari {sourceName}"
                : "belum tersedia dari sumber yang terverifikasi";

        return new List<DailyReportSourceNote>
        {
            new()
            {
                Metric = "Reach",
                Definition = "Total viewers unik level akun untuk seluruh periode (bukan jumlah reach per-video atau " +
                    "penjumlahan reach harian - reach harian tidak pernah diubah jadi reach mingguan unik).",
                Status = StatusFor(DailyReportMetrics.Reach)
            },
            new()
            {
                Metric = "Video Views",
                Definition = "Total Video Views level akun per minggu (Senin-Minggu, dipotong ke rentang terpilih), " +
                    "diakumulasi dari agregat harian terverifikasi pada jendela minggu tersebut.",
                Status = StatusFor(DailyReportMetrics.VideoViews)
            },
            new()
            {
                Metric = "Profile Views",
                Definition = "Total Profile Views level akun per minggu dari agregat harian terverifikasi.",
                Status = StatusFor(DailyReportMetrics.ProfileViews)
            },
            new()
            {
                Metric = "Interactions",
                Definition = "Likes + Comments + Shares per minggu, memakai total agregat untuk TEPAT jendela minggu " +
                    "tersebut (analytics/202605 video list). Total jendela yang lebih besar tidak pernah dibagi rata " +
                    "ke beberapa minggu dan tidak pernah dijumlah ganda dengan nilai harian.",
                Status = StatusFor(DailyReportMetrics.Interactions)
            },
            new()
            {
                Metric = "Engagement Rate",
                Definition = "Total Interactions periode / Total Reach periode x 100% (bukan rerata ER mingguan) - " +
                    "Reach sebagai pembagi; selama Reach belum tersedia nilai tetap \"-\" (tidak diganti Views).",
                Status = "dihitung oleh dashboard"
            },
            new()
            {
                Metric = "Net Follower",
                Definition = "Followers Gained - Followers Lost level akun per minggu, lalu diakumulasi per periode - " +
                    "keuntungan follower per-video tidak pernah dijumlah jadi Net Follower akun.",
                Status = StatusFor(DailyReportMetrics.NetFollower)
            },
            new()
            {
                Metric = "Total Follower",
                Definition = "Snapshot follower pada posisi akhir periode - penyimpanan snapshot internal belum tersedia, " +
                    "sehingga nilai selalu \"-\" (tanpa migrasi database baru).",
                Status = StatusFor(DailyReportMetrics.TotalFollower)
            },
            new()
            {
                Metric = "Periode Pembanding",
                Definition = "Rentang sama panjang yang berakhir tepat sehari sebelum StartDate (apples-to-apples, " +
                    "tanpa overlap) - konvensi yang sama dengan tab Daily.",
                Status = "dihitung oleh dashboard"
            },
            new()
            {
                Metric = "Bucketing minggu",
                Definition = "Minggu = Senin-Minggu (Asia/Jakarta); rentang terpilih dipotong per minggu tanpa " +
                    "snapping - kolom Hari menunjukkan jumlah hari tercakup dan tanda parsial menandai minggu yang tidak penuh.",
                Status = "dihitung oleh dashboard"
            }
        };
    }
}
