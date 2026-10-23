using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// DI today is <see cref="TikTokShopVideoOverviewMetricSource"/>, which maps ONLY the
/// verified daily video_views field of the shop-video overview API and declares that
/// scope through <see cref="AccountDailyMetricDataset.AvailableMetricKeys"/>. Every
/// metric outside that declared set is rendered as Unavailable ("-") with an honest
/// reason - never "no data recorded", never populated from unrelated fields. The
/// previous <see cref="UnavailableAccountDailyMetricSource"/> remains the documented
/// fallback for environments without verified sources.
///
/// 2026-10-21 extension: the same verified endpoint also supplies the commerce-funnel
/// record-level fields product_impressions, product_clicks and video_attributed_sku_orders.
/// Those three are now surfaced as their OWN summary cards + comparison rows + shop-video
/// detail panel. They are NOT folded into Reach/Interactions/Engagement Rate/follower
/// families (reach and interactions remain profile-engagement metrics only). The source
/// reads them from the identical daily interval that carries video_views, so no new API
/// call or new provider is introduced here.
/// </summary>
public sealed class DailyPerformanceService : IDailyPerformanceService
{
    private const string NoDataReason = "Belum ada data tercatat pada periode ini.";

    private readonly IAccountDailyMetricSource _source;
    private readonly IShopTimeZone _shopTimeZone;
    private readonly ILogger<DailyPerformanceService> _logger;

    public DailyPerformanceService(
        IAccountDailyMetricSource source,
        IShopTimeZone shopTimeZone,
        ILogger<DailyPerformanceService>? logger = null)
    {
        _source = source;
        _shopTimeZone = shopTimeZone;
        _logger = logger ?? NullLogger<DailyPerformanceService>.Instance;
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
        var shopVideoMetrics = BuildShopVideoMetrics(currentContext, previousContext);
        var rows = BuildRows(currentContext);

        // Safe per-build diagnostics (no tokens/secrets): which period was built, how
        // many daily records survived date filtering and the final Video Views card
        // state - enough to locate a suppression without a debugger.
        var videoViewsCard = cards.Single(c => c.Key == DailyReportMetrics.VideoViews);
        _logger.LogInformation(
            "Daily Report built {Start}..{End}: sourceAvailable={SourceAvailable}, recordsInCurrentPeriod={Records}, VideoViews={Value} ({Status}), previous VideoViews={PreviousValue} ({PreviousStatus})",
            current.Start.ToString("yyyy-MM-dd"),
            current.End.ToString("yyyy-MM-dd"),
            currentResult.IsAvailable,
            currentContext.RecordsByDate.Count,
            videoViewsCard.CurrentValue,
            videoViewsCard.CurrentStatus,
            videoViewsCard.PreviousValue,
            videoViewsCard.PreviousStatus);

        var interactionComposition = BuildInteractionComposition(currentContext);
        var viewerComposition = BuildViewerComposition(currentContext);

        var notices = BuildNotices(currentResult, previousResult, cards.Concat(shopVideoMetrics).ToList());

        // Honest delta-suppression explanation (fix 2026-10-23): when the CURRENT period
        // carries Interactions but the PREVIOUS one does not (comparison fetch failed,
        // metric not declared for that window, or genuinely no data), the comparison
        // columns render "-" - the operator is told WHY instead of seeing a silent dash.
        // A current value is NEVER copied into the previous column and a missing
        // previous is NEVER treated as zero.
        var interactionsCard = cards.Single(c => c.Key == DailyReportMetrics.Interactions);
        if (interactionsCard.CurrentValue.HasValue && !interactionsCard.PreviousValue.HasValue)
        {
            string? previousReason;
            if (!previousContext.SourceAvailable)
            {
                previousReason = previousContext.SourceUnavailableReason;
            }
            else
            {
                previousReason = previousContext.Interactions.Status switch
                {
                    DailyMetricStatus.Unavailable => previousContext.LimitedSourceReason ?? previousContext.SourceUnavailableReason,
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
            // Shop-video commerce funnel: rendered in its own section, never mixed
            // into the account summary cards or the account comparison rows.
            ShopVideoMetrics = shopVideoMetrics,
            ComparisonRows = cards.Where(c => c.ShowInComparison).ToList(),
            // Same row objects, opposite order: charts read ascending, the table descending.
            Trend = rows,
            DailyRows = rows.AsEnumerable().Reverse().ToList(),
            InteractionComposition = interactionComposition,
            ViewerComposition = viewerComposition,
            Notices = notices,
            SourceNotes = BuildSourceNotes(currentResult),
            HasAnyMetricData = cards.Concat(shopVideoMetrics).Any(c => c.CurrentValue.HasValue)
        };
    }

    // ------------------------------------------------------------------
    // Period evaluation
    // ------------------------------------------------------------------

    /// <summary>Metric keys of the summary cards, with their display labels (mockup order).</summary>
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
    /// Additional commerce-funnel metrics surfaced on their own cards and daily rows.
    /// They are sourced from the SAME verified TikTok Shop Video Performance Overview
    /// endpoint that carries video_views, and they are NOT mapped to Reach / Interactions /
    /// Engagement Rate / follower families.
    /// </summary>
    private static readonly (string Key, string Label)[] ShopCardMetrics =
    {
        (DailyReportMetrics.ProductImpressions, "Product Impressions"),
        (DailyReportMetrics.ProductClicks, "Product Clicks"),
        (DailyReportMetrics.VideoAttributedSkuOrders, "Video-attributed SKU Orders"),
        (DailyReportMetrics.VideoAttributedGmv, "Video-attributed GMV"),
        (DailyReportMetrics.VideoGmv, "Video GMV"),
        (DailyReportMetrics.VideoIndirectGmv, "Video Indirect GMV"),
        (DailyReportMetrics.Gpm, "GPM"),
        (DailyReportMetrics.ProductCtr, "Product CTR"),
        (DailyReportMetrics.CtorSkuOrder, "CTOR SKU Order"),
        (DailyReportMetrics.VideoProductViewers, "Video Product Viewers"),
        (DailyReportMetrics.AvgDailyCustomers, "Avg Daily Customers")
    };

    /// <summary>All aggregates of one period, evaluated once and reused everywhere.</summary>
    private sealed class PeriodContext
    {
        public DailyReportDateRange Range { get; init; }
        public bool SourceAvailable { get; init; }
        public string SourceUnavailableReason { get; init; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;
        public int DaysInPeriod { get; init; }

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

        public PeriodValue? EngagementRate { get; init; }
        public PeriodValue? TotalFollower { get; init; }

        /// <summary>
        /// Additional commerce-funnel period values (Product Impressions, Product Clicks,
        /// Video-attributed SKU Orders). Same aggregation contract as the other absolute
        /// metrics - never Reach/Interactions/follower substitutes.
        /// </summary>
        public PeriodValue? ProductImpressions { get; init; }
        public PeriodValue? ProductClicks { get; init; }
        public PeriodValue? VideoAttributedSkuOrders { get; init; }

        // ---- Verified shop-video analytics extras (period aggregates) ----
        // CTR / CTOR are recomputed from period SUMS (never averaged daily rates);
        // GPM = period video_attributed_gmv / period video_views x 1000 (verified exact
        // per-day, so the summed form is exact too); GMV subtypes are summed per subtype
        // only and keep their currency; avg_daily_customers is the mean over covered days.
        public PeriodValue? ProductCtr { get; init; }
        public PeriodValue? CtorSkuOrder { get; init; }
        public PeriodValue? VideoAttributedGmv { get; init; }
        public PeriodValue? VideoGmv { get; init; }
        public PeriodValue? VideoIndirectGmv { get; init; }
        public PeriodValue? Gpm { get; init; }
        public PeriodValue? VideoProductViewers { get; init; }
        public PeriodValue? AvgDailyCustomers { get; init; }

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
        int DaysInPeriod,
        string? Currency = null)
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
                TotalFollower = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                ProductImpressions = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                ProductClicks = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                VideoAttributedSkuOrders = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                VideoAttributedGmv = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                VideoGmv = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                VideoIndirectGmv = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                Gpm = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                ProductCtr = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                CtorSkuOrder = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                VideoProductViewers = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count),
                AvgDailyCustomers = new PeriodValue(null, DailyMetricStatus.Unavailable, reason, 0, days.Count)
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

        // Declared metric scope of this dataset (null = complete dataset, every metric).
        // A VERIFIED PARTIAL source (e.g. only daily video_views) declares exactly its
        // keys; every other metric is then rendered Unavailable with an honest reason
        // instead of "no data recorded", and its per-day values are forced to null so
        // a value from an unrelated field can never leak into it.
        var availableKeys = result.Dataset.AvailableMetricKeys;
        var limitedReason = availableKeys is null
            ? null
            : $"Metrik ini belum memiliki sumber data terverifikasi dan ditampilkan \"-\". " +
              $"Sumber saat ini hanya menyediakan: {string.Join(", ", CardMetrics.Concat(ShopCardMetrics).Where(m => availableKeys.Contains(m.Key)).Select(m => m.Label))}.";

        bool Supplies(string key) => availableKeys is null || availableKeys.Contains(key);

        var reach = new List<long?>(days.Count);
        var videoViews = new List<long?>(days.Count);
        var profileViews = new List<long?>(days.Count);
        var likes = new List<long?>(days.Count);
        var comments = new List<long?>(days.Count);
        var shares = new List<long?>(days.Count);
        var interactions = new List<long?>(days.Count);
        var netFollower = new List<long?>(days.Count);
        var productImpressions = new List<long?>(days.Count);
        var productClicks = new List<long?>(days.Count);
        var videoAttributedSkuOrders = new List<long?>(days.Count);

        // Shop-video analytics extras of the same verified overview interval. Monetary
        // values travel with their currency; rates stay FRACTIONS here (percent points
        // only at the presentation boundary, same convention as EngagementRate).
        var attributedGmv = new List<(decimal? Amount, string? Currency)>(days.Count);
        var videoGmv = new List<(decimal? Amount, string? Currency)>(days.Count);
        var indirectGmv = new List<(decimal? Amount, string? Currency)>(days.Count);
        var gpmAmounts = new List<(decimal? Amount, string? Currency)>(days.Count);
        var videoProductViewersDaily = new List<long?>(days.Count);
        var avgDailyCustomersDaily = new List<decimal?>(days.Count);

        foreach (var day in days)
        {
            var record = records.GetValueOrDefault(day);

            reach.Add(Supplies(DailyReportMetrics.Reach) ? record?.Reach : null);
            videoViews.Add(Supplies(DailyReportMetrics.VideoViews) ? record?.VideoViews : null);
            profileViews.Add(Supplies(DailyReportMetrics.ProfileViews) ? record?.ProfileViews : null);
            likes.Add(record?.Likes);
            comments.Add(record?.Comments);
            shares.Add(record?.Shares);
            // Composite metrics: a record missing any component yields null, never a partial total.
            interactions.Add(record is null || !Supplies(DailyReportMetrics.Interactions)
                ? null
                : DailyReportCalculator.Interactions(record.Likes, record.Comments, record.Shares));
            netFollower.Add(record is null || !Supplies(DailyReportMetrics.NetFollower)
                ? null
                : DailyReportCalculator.NetFollower(record.FollowersGained, record.FollowersLost));
            // Additional shop-video commerce metrics (Funnel): aggregated per the same
            // verified record-level fields, never mapped to Reach/Interactions/follower families.
            productImpressions.Add(record is null || !Supplies(DailyReportMetrics.ProductImpressions)
                ? null
                : record.ProductImpressions);
            productClicks.Add(record is null || !Supplies(DailyReportMetrics.ProductClicks)
                ? null
                : record.ProductClicks);
            videoAttributedSkuOrders.Add(record is null || !Supplies(DailyReportMetrics.VideoAttributedSkuOrders)
                ? null
                : record.VideoAttributedSkuOrders);

            attributedGmv.Add(Supplies(DailyReportMetrics.VideoAttributedGmv) && record?.VideoAttributedGmvAmount is not null
                ? (record.VideoAttributedGmvAmount, record.VideoAttributedGmvCurrency)
                : (null, null));
            videoGmv.Add(Supplies(DailyReportMetrics.VideoGmv) && record?.VideoGmvAmount is not null
                ? (record.VideoGmvAmount, record.VideoGmvCurrency)
                : (null, null));
            indirectGmv.Add(Supplies(DailyReportMetrics.VideoIndirectGmv) && record?.VideoIndirectGmvAmount is not null
                ? (record.VideoIndirectGmvAmount, record.VideoIndirectGmvCurrency)
                : (null, null));
            gpmAmounts.Add(Supplies(DailyReportMetrics.Gpm) && record?.GpmAmount is not null
                ? (record.GpmAmount, record.GpmCurrency)
                : (null, null));
            videoProductViewersDaily.Add(Supplies(DailyReportMetrics.VideoProductViewers)
                ? record?.VideoProductViewers
                : null);
            avgDailyCustomersDaily.Add(Supplies(DailyReportMetrics.AvgDailyCustomers)
                ? record?.AvgDailyCustomers
                : null);
        }

        var sourceReason = string.Empty;
        var reachAgg = Supplies(DailyReportMetrics.Reach)
            ? DailyReportCalculator.Sum(reach, days.Count)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);

        // Interactions: the EXACT period totals supplied by the source (windowed sums over
        // the whole half-open window - complete by construction) win over the daily sums.
        // Daily sums remain the fallback for sources that carry per-day components. A period
        // aggregate is NEVER spread across individual days: daily rows keep their own
        // record-level values (null when the source has no daily breakdown for them).
        var periodInteractions = result.Dataset.PeriodInteractions;
        var hasPeriodInteractions = periodInteractions is not null
            && periodInteractions.Likes.HasValue
            && periodInteractions.Comments.HasValue
            && periodInteractions.Shares.HasValue;

        var interactionsAgg = !Supplies(DailyReportMetrics.Interactions)
            ? DailyMetricAggregate.None(DailyMetricStatus.Unavailable)
            : hasPeriodInteractions
                ? new DailyMetricAggregate(
                    periodInteractions!.Likes!.Value + periodInteractions.Comments!.Value + periodInteractions.Shares!.Value,
                    days.Count,
                    DailyMetricStatus.Complete)
                : DailyReportCalculator.Sum(interactions, days.Count);

        // Period ER = Total Interactions / Total Reach x 100 (never the average of daily ERs).
        PeriodValue engagementRate;
        if (interactionsAgg.Value is null || reachAgg.Value is null)
        {
            var status = DailyReportCalculator.Worst(interactionsAgg.Status, reachAgg.Status);
            // When Reach is the missing side, say WHY with the locked formula - the
            // denominator stays Reach and is never swapped for Views to fill the card.
            var erReason = reachAgg.Status == DailyMetricStatus.Unavailable
                ? "Engagement Rate membutuhkan Reach (formula Interactions / Reach x 100%) dan Reach belum memiliki " +
                  "sumber data terverifikasi - nilai tetap ditampilkan \"-\" (bukan diganti dengan Views)."
                : status == DailyMetricStatus.Unavailable ? limitedReason ?? sourceReason : NoDataReason;
            engagementRate = new PeriodValue(
                null,
                status,
                erReason,
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
        var snapshot = Supplies(DailyReportMetrics.TotalFollower)
            ? DailyReportCalculator.FollowerSnapshotAtOrBefore(
                result.Dataset.FollowerSnapshots
                    .Where(s => s.AsOfDate.Date >= range.Start)
                    .ToList(),
                range.End)
            : null;

        var totalFollower = !Supplies(DailyReportMetrics.TotalFollower)
            ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count)
            : snapshot is null
            ? new PeriodValue(
                null,
                DailyMetricStatus.NoData,
                "Tidak ada snapshot follower yang valid pada periode ini (snapshot setelah EndDate tidak pernah digunakan).",
                0,
                days.Count)
            : new PeriodValue(snapshot.FollowerCount, DailyMetricStatus.Complete, null, days.Count, days.Count);

        // ---- Shop-video analytics extras (same verified overview interval) ----
        // Aggregation follows the FIELD-VERIFIED TikTok definitions (2026-10-22 live
        // response): ctr = clicks/impressions, ctor = orders/clicks, gpm = attributed
        // GMV/views x 1000. Rates are aggregated with their REAL denominators - never
        // by averaging daily rates. GMV subtypes are summed per subtype only.
        var videoViewsAgg = Supplies(DailyReportMetrics.VideoViews)
            ? DailyReportCalculator.Sum(videoViews, days.Count)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var impressionsAgg = Supplies(DailyReportMetrics.ProductImpressions)
            ? DailyReportCalculator.Sum(productImpressions, days.Count)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var clicksAgg = Supplies(DailyReportMetrics.ProductClicks)
            ? DailyReportCalculator.Sum(productClicks, days.Count)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var ordersAgg = Supplies(DailyReportMetrics.VideoAttributedSkuOrders)
            ? DailyReportCalculator.Sum(videoAttributedSkuOrders, days.Count)
            : DailyMetricAggregate.None(DailyMetricStatus.Unavailable);
        var attributedGmvSum = SumMoney(attributedGmv, days.Count);
        var videoGmvSum = SumMoney(videoGmv, days.Count);
        var indirectGmvSum = SumMoney(indirectGmv, days.Count);

        // Local ratio helper: numerator/denominator x multiplier (percent points or per-mille).
        // Zero/absent denominator -> null with an honest reason, never a fabricated rate.
        PeriodValue Ratio(
            decimal? numerator, DailyMetricStatus numeratorStatus, int numeratorDays,
            long? denominator, DailyMetricStatus denominatorStatus, int denominatorDays,
            decimal multiplier, string zeroDenominatorReason, string? currency = null)
        {
            if (numerator is null || denominator is null)
            {
                var status = DailyReportCalculator.Worst(numeratorStatus, denominatorStatus);
                return new PeriodValue(null, status,
                    status == DailyMetricStatus.Unavailable ? limitedReason : NoDataReason,
                    0, days.Count);
            }

            if (denominator.Value <= 0)
            {
                return new PeriodValue(null, DailyMetricStatus.NoData, zeroDenominatorReason,
                    Math.Min(numeratorDays, denominatorDays), days.Count);
            }

            return new PeriodValue(
                numerator.Value / denominator.Value * multiplier,
                DailyReportCalculator.Worst(numeratorStatus, denominatorStatus),
                null,
                Math.Min(numeratorDays, denominatorDays),
                days.Count,
                currency);
        }

        PeriodValue MoneyPeriodValue(MoneySum sum, string? unavailableReason)
            => sum.Status == DailyMetricStatus.Unavailable
                ? new PeriodValue(null, DailyMetricStatus.Unavailable, unavailableReason, 0, days.Count)
                : sum.Sum is null
                    ? new PeriodValue(null, sum.Status, sum.Reason ?? NoDataReason, 0, days.Count)
                    : new PeriodValue(sum.Sum, sum.Status, null, sum.DaysCovered, days.Count, sum.Currency);

        var productCtrValue = !Supplies(DailyReportMetrics.ProductCtr)
            ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count)
            : Ratio(clicksAgg.Value, clicksAgg.Status, clicksAgg.DaysCovered,                impressionsAgg.Value, impressionsAgg.Status, impressionsAgg.DaysCovered,
                100m,
                "Product CTR tidak dapat dihitung karena Product Impressions periode ini tidak tersedia atau 0.");

        var ctorSkuOrderValue = !Supplies(DailyReportMetrics.CtorSkuOrder)
            ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count)
            : Ratio(ordersAgg.Value, ordersAgg.Status, ordersAgg.DaysCovered,
                clicksAgg.Value, clicksAgg.Status, clicksAgg.DaysCovered,
                100m,
                "CTOR SKU Order tidak dapat dihitung karena Product Clicks periode ini tidak tersedia atau 0.");

        // GPM = attributed GMV / Video Views x 1000 (currency preserved). The Ratio
        // helper handles a null/absent numerator (unavailable or no-data GMV) honestly.
        var gpmValue = !Supplies(DailyReportMetrics.Gpm)
            ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count)
            : Ratio(attributedGmvSum.Sum, attributedGmvSum.Status, attributedGmvSum.DaysCovered,
                videoViewsAgg.Value, videoViewsAgg.Status, videoViewsAgg.DaysCovered,
                1000m,
                "GPM tidak dapat dihitung karena Video Views periode ini tidak tersedia atau 0.",
                attributedGmvSum.Currency);

        var videoProductViewersValue = !Supplies(DailyReportMetrics.VideoProductViewers)
            ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count)
            : periodCommerceValue(videoProductViewersDaily, days.Count);

        var adcCovered = avgDailyCustomersDaily.Count(v => v.HasValue);
        var avgDailyCustomersValue = !Supplies(DailyReportMetrics.AvgDailyCustomers)
            ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count)
            : adcCovered == 0
                ? new PeriodValue(null, DailyMetricStatus.NoData, NoDataReason, 0, days.Count)
                : new PeriodValue(
                    avgDailyCustomersDaily.Where(v => v.HasValue).Sum(v => v!.Value) / adcCovered,
                    adcCovered == days.Count ? DailyMetricStatus.Complete : DailyMetricStatus.Partial,
                    null,
                    adcCovered,
                    days.Count);

        return new PeriodContext
        {
            Range = range,
            DaysInPeriod = days.Count,
            SourceAvailable = true,
            SourceUnavailableReason = sourceReason,
            SourceName = result.Dataset.SourceName,
            RecordsByDate = records,
            AvailableMetricKeys = availableKeys,
            LimitedSourceReason = limitedReason,
            Reach = reachAgg,
            VideoViews = Supplies(DailyReportMetrics.VideoViews)
                ? DailyReportCalculator.Sum(videoViews, days.Count)
                : DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            ProfileViews = Supplies(DailyReportMetrics.ProfileViews)
                ? DailyReportCalculator.Sum(profileViews, days.Count)
                : DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            Likes = hasPeriodInteractions
                ? new DailyMetricAggregate(periodInteractions!.Likes, days.Count, DailyMetricStatus.Complete)
                : DailyReportCalculator.Sum(likes, days.Count),
            Comments = hasPeriodInteractions
                ? new DailyMetricAggregate(periodInteractions!.Comments, days.Count, DailyMetricStatus.Complete)
                : DailyReportCalculator.Sum(comments, days.Count),
            Shares = hasPeriodInteractions
                ? new DailyMetricAggregate(periodInteractions!.Shares, days.Count, DailyMetricStatus.Complete)
                : DailyReportCalculator.Sum(shares, days.Count),
            Interactions = interactionsAgg,
            NetFollower = Supplies(DailyReportMetrics.NetFollower)
                ? DailyReportCalculator.Sum(netFollower, days.Count)
                : DailyMetricAggregate.None(DailyMetricStatus.Unavailable),
            EngagementRate = engagementRate,
            TotalFollower = totalFollower,
            ViewerComposition = result.Dataset.ViewerComposition,
            ProductImpressions = Supplies(DailyReportMetrics.ProductImpressions)
                ? periodCommerceValue(productImpressions, days.Count)
                : new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count),
            ProductClicks = Supplies(DailyReportMetrics.ProductClicks)
                ? periodCommerceValue(productClicks, days.Count)
                : new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count),
            VideoAttributedSkuOrders = Supplies(DailyReportMetrics.VideoAttributedSkuOrders)
                ? periodCommerceValue(videoAttributedSkuOrders, days.Count)
                : new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count),
            VideoAttributedGmv = !Supplies(DailyReportMetrics.VideoAttributedGmv)
                ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count)
                : MoneyPeriodValue(attributedGmvSum, limitedReason),
            VideoGmv = !Supplies(DailyReportMetrics.VideoGmv)
                ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count)
                : MoneyPeriodValue(videoGmvSum, limitedReason),
            VideoIndirectGmv = !Supplies(DailyReportMetrics.VideoIndirectGmv)
                ? new PeriodValue(null, DailyMetricStatus.Unavailable, limitedReason, 0, days.Count)
                : MoneyPeriodValue(indirectGmvSum, limitedReason),
            Gpm = gpmValue,
            ProductCtr = productCtrValue,
            CtorSkuOrder = ctorSkuOrderValue,
            VideoProductViewers = videoProductViewersValue,
            AvgDailyCustomers = avgDailyCustomersValue
        };
    }

    private static PeriodValue ToValue(DailyMetricAggregate aggregate, int daysInPeriod, string sourceReason)
        => aggregate.Status switch
        {
            DailyMetricStatus.Unavailable => new PeriodValue(null, DailyMetricStatus.Unavailable, sourceReason, 0, daysInPeriod),
            DailyMetricStatus.NoData => new PeriodValue(null, DailyMetricStatus.NoData, NoDataReason, 0, daysInPeriod),
            _ => new PeriodValue(aggregate.Value, aggregate.Status, null, aggregate.DaysCovered, daysInPeriod)
        };

    private static PeriodValue periodCommerceValue(List<long?> values, int daysInPeriod)
    {
        var covered = values.Count(v => v.HasValue);
        var sum = values.Where(v => v.HasValue).Select(v => v!.Value).Aggregate(0L, (acc, v) => acc + v);
        var status = covered == daysInPeriod
            ? DailyMetricStatus.Complete
            : covered > 0
                ? DailyMetricStatus.Partial
                : DailyMetricStatus.NoData;

        return new PeriodValue(
            covered > 0 ? sum : null,
            status,
            status == DailyMetricStatus.NoData ? NoDataReason : null,
            covered,
            daysInPeriod);
    }

    /// <summary>Result of summing monetary daily values for one period.</summary>
    private sealed record MoneySum(
        decimal? Sum,
        string? Currency,
        DailyMetricStatus Status,
        int DaysCovered,
        string? Reason);

    /// <summary>
    /// Sums monetary daily values (GMV subtypes, GPM numerator) for the period.
    /// A period total is only produced when the covered days carry ONE consistent
    /// currency; mixed or missing currencies yield NoData with an honest reason so
    /// amounts in different currencies are never silently added together.
    /// </summary>
    private static MoneySum SumMoney(List<(decimal? Amount, string? Currency)> values, int daysInPeriod)
    {
        var coveredDays = values.Where(v => v.Amount.HasValue).ToList();
        if (coveredDays.Count == 0)
        {
            return new MoneySum(null, null, DailyMetricStatus.NoData, 0, NoDataReason);
        }

        var currencies = coveredDays
            .Select(v => v.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (currencies.Count != 1 || string.IsNullOrWhiteSpace(currencies[0]))
        {
            return new MoneySum(null, null, DailyMetricStatus.NoData, coveredDays.Count,
                "Mata uang nilai tidak konsisten antar hari - total periode tidak dihitung.");
        }

        var sum = coveredDays.Sum(v => v.Amount!.Value);
        var status = coveredDays.Count == daysInPeriod
            ? DailyMetricStatus.Complete
            : DailyMetricStatus.Partial;
        return new MoneySum(sum, currencies[0], status, coveredDays.Count, null);
    }

    // ------------------------------------------------------------------
    // Cards + comparison rows
    // ------------------------------------------------------------------

    private static IReadOnlyList<DailyReportMetric> BuildCards(PeriodContext current, PeriodContext previous)
    {
        var sourceReason = current.SourceUnavailableReason;

        // A fully unavailable source explains itself with SourceUnavailableReason; a
        // limited (verified-partial) source explains each unsupplied metric with the
        // LimitedSourceReason instead.
        PeriodValue Cur(DailyMetricAggregate agg) => ToValue(
            agg,
            current.DaysInPeriod,
            agg.Status == DailyMetricStatus.Unavailable && string.IsNullOrEmpty(sourceReason)
                ? current.LimitedSourceReason ?? string.Empty
                : sourceReason);
        PeriodValue Prev(DailyMetricAggregate agg) => ToValue(
            agg,
            previous.DaysInPeriod,
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
            new PeriodValue(null, DailyMetricStatus.NoData, null, 0, previous.DaysInPeriod),
            DailyMetricDeltaKind.PositionOnly,
            showInComparison: false));

        return cards;
    }

    /// <summary>
    /// Shop-video metrics (commerce funnel + verified analytics extras). They get their
    /// OWN section and their own comparison block: they are NOT Reach / Interactions /
    /// Engagement Rate / follower metrics, so they never enter the account summary cards
    /// or the account comparison rows. Rate metrics use PercentagePoints deltas; monetary
    /// metrics carry their currency so an amount is never rendered bare.
    /// </summary>
    private static IReadOnlyList<DailyReportMetric> BuildShopVideoMetrics(PeriodContext current, PeriodContext previous)
    {
        DailyReportMetric Shop(string key, string label, PeriodValue? cur, PeriodValue? prev, DailyMetricDeltaKind deltaKind)
            => Build(key, label, cur!, prev!, deltaKind, currency: cur?.Currency ?? prev?.Currency);

        return new List<DailyReportMetric>
        {
            Shop(DailyReportMetrics.ProductImpressions, "Product Impressions",
                current.ProductImpressions, previous.ProductImpressions, DailyMetricDeltaKind.Absolute),
            Shop(DailyReportMetrics.ProductClicks, "Product Clicks",
                current.ProductClicks, previous.ProductClicks, DailyMetricDeltaKind.Absolute),
            Shop(DailyReportMetrics.VideoAttributedSkuOrders, "Video-attributed SKU Orders",
                current.VideoAttributedSkuOrders, previous.VideoAttributedSkuOrders, DailyMetricDeltaKind.Absolute),
            Shop(DailyReportMetrics.VideoAttributedGmv, "Video-attributed GMV",
                current.VideoAttributedGmv, previous.VideoAttributedGmv, DailyMetricDeltaKind.Absolute),
            Shop(DailyReportMetrics.VideoGmv, "Video GMV",
                current.VideoGmv, previous.VideoGmv, DailyMetricDeltaKind.Absolute),
            Shop(DailyReportMetrics.VideoIndirectGmv, "Video Indirect GMV",
                current.VideoIndirectGmv, previous.VideoIndirectGmv, DailyMetricDeltaKind.Absolute),
            Shop(DailyReportMetrics.Gpm, "GPM",
                current.Gpm, previous.Gpm, DailyMetricDeltaKind.Absolute),
            Shop(DailyReportMetrics.ProductCtr, "Product CTR",
                current.ProductCtr, previous.ProductCtr, DailyMetricDeltaKind.PercentagePoints),
            Shop(DailyReportMetrics.CtorSkuOrder, "CTOR SKU Order",
                current.CtorSkuOrder, previous.CtorSkuOrder, DailyMetricDeltaKind.PercentagePoints),
            Shop(DailyReportMetrics.VideoProductViewers, "Video Product Viewers",
                current.VideoProductViewers, previous.VideoProductViewers, DailyMetricDeltaKind.Absolute),
            Shop(DailyReportMetrics.AvgDailyCustomers, "Avg Daily Customers",
                current.AvgDailyCustomers, previous.AvgDailyCustomers, DailyMetricDeltaKind.Absolute)
        };
    }

    private static DailyReportMetric Build(
        string key,
        string label,
        PeriodValue current,
        PeriodValue previous,
        DailyMetricDeltaKind deltaKind,
        bool showInComparison = true,
        string? currency = null)
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
            ShowInComparison = showInComparison,
            Currency = currency
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

            var reach = context.Supplies(DailyReportMetrics.Reach) ? record?.Reach : null;
            var interactions = record is null || !context.Supplies(DailyReportMetrics.Interactions)
                ? null
                : DailyReportCalculator.Interactions(record.Likes, record.Comments, record.Shares);

            rows.Add(new DailyReportRow
            {
                Date = day,
                Reach = reach,
                VideoViews = context.Supplies(DailyReportMetrics.VideoViews) ? record?.VideoViews : null,
                ProfileViews = context.Supplies(DailyReportMetrics.ProfileViews) ? record?.ProfileViews : null,
                Interactions = interactions,
                EngagementRate = DailyReportCalculator.EngagementRate(interactions, reach),
                NetFollower = record is null || !context.Supplies(DailyReportMetrics.NetFollower)
                    ? null
                    : DailyReportCalculator.NetFollower(record.FollowersGained, record.FollowersLost),
                // Shop-video commerce funnel of the day (null = not recorded, never 0).
                ProductImpressions = context.Supplies(DailyReportMetrics.ProductImpressions) ? record?.ProductImpressions : null,
                ProductClicks = context.Supplies(DailyReportMetrics.ProductClicks) ? record?.ProductClicks : null,
                VideoAttributedSkuOrders = context.Supplies(DailyReportMetrics.VideoAttributedSkuOrders) ? record?.VideoAttributedSkuOrders : null,
                // Shop-video analytics extras of the day. Rates are converted from the
                // stored 0..1 fractions to PERCENT POINTS at this presentation boundary
                // (same convention as EngagementRate); monetary values keep their currency.
                ProductCtrPercent = context.Supplies(DailyReportMetrics.ProductCtr) && record?.Ctr is not null
                    ? record.Ctr.Value * 100m
                    : null,
                CtorSkuOrderPercent = context.Supplies(DailyReportMetrics.CtorSkuOrder) && record?.CtorSkuOrder is not null
                    ? record.CtorSkuOrder.Value * 100m
                    : null,
                VideoAttributedGmvAmount = context.Supplies(DailyReportMetrics.VideoAttributedGmv) ? record?.VideoAttributedGmvAmount : null,
                VideoAttributedGmvCurrency = context.Supplies(DailyReportMetrics.VideoAttributedGmv) ? record?.VideoAttributedGmvCurrency : null,
                VideoGmvAmount = context.Supplies(DailyReportMetrics.VideoGmv) ? record?.VideoGmvAmount : null,
                VideoGmvCurrency = context.Supplies(DailyReportMetrics.VideoGmv) ? record?.VideoGmvCurrency : null,
                VideoIndirectGmvAmount = context.Supplies(DailyReportMetrics.VideoIndirectGmv) ? record?.VideoIndirectGmvAmount : null,
                VideoIndirectGmvCurrency = context.Supplies(DailyReportMetrics.VideoIndirectGmv) ? record?.VideoIndirectGmvCurrency : null,
                GpmAmount = context.Supplies(DailyReportMetrics.Gpm) ? record?.GpmAmount : null,
                GpmCurrency = context.Supplies(DailyReportMetrics.Gpm) ? record?.GpmCurrency : null,
                VideoProductViewers = context.Supplies(DailyReportMetrics.VideoProductViewers) ? record?.VideoProductViewers : null,
                AvgDailyCustomers = context.Supplies(DailyReportMetrics.AvgDailyCustomers) ? record?.AvgDailyCustomers : null,
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

        // A source that does not declare Interactions must never render "no data recorded"
        // for the composition - it is UNAVAILABLE, and the honest limited-source reason says so.
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
            return Unavailable(key, title, partial
                ? $"Komponen interaksi tidak lengkap pada periode ini ({context.Interactions.DaysCovered} dari {context.DaysInPeriod} hari) - komposisi tidak ditampilkan agar tidak menyesatkan."
                : unavailable
                    ? context.LimitedSourceReason ?? context.SourceUnavailableReason
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
                context.LimitedSourceReason is null
                    ? "Komposisi viewer baru/lama level akun belum tersedia dari sumber yang terverifikasi."
                    : context.LimitedSourceReason,
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
            // Surface the provider's ACTUAL reason (transport failure, HTTP/business
            // error, missing configuration...). Proven 2026-10-21: a transport failure
            // previously rendered a notice that only said the source was "not
            // integrated", hiding the real cause from the operator.
            var reason = string.IsNullOrWhiteSpace(currentResult.UnavailableReason)
                ? UnavailableAccountDailyMetricSource.Reason
                : currentResult.UnavailableReason!;
            notices.Add(
                "Seluruh metrik ditampilkan \"-\" karena sumber analytics level akun (harian) belum terintegrasi " +
                "atau belum dapat dihubungi pada permintaan ini. Detail: " + reason);
        }
        else
        {
            if (!previousResult.IsAvailable)
            {
                notices.Add("Periode pembanding tidak tersedia sehingga kolom Periode Sebelumnya dan Selisih ditampilkan \"-\".");
            }

            // Verified-partial source: some metric families are still without a verified
            // source and must be called out explicitly instead of silently showing "-".
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
        var sourceName = currentResult.Dataset?.SourceName ?? "sumber account-level";
        var availableKeys = currentResult.Dataset?.AvailableMetricKeys;

        // Per-metric footer status: a metric is only "tersedia" when the registered
        // provider explicitly declares it (verified mapping). Everything else is
        // honestly reported as not-yet-verified, even when the source itself answers.
        string StatusFor(string key) => !currentResult.IsAvailable
            ? "belum tersambung (blocked)"
            : availableKeys is null || availableKeys.Contains(key)
                ? $"tersedia dari {sourceName}"
                : "belum tersedia dari sumber yang terverifikasi";

        return new List<DailyReportSourceNote>
        {
            new()
            {
                Metric = "Reach",
                Definition = "Agregasi harian Reach / Total Viewers level akun (bukan jumlah Reach per-video).",
                Status = StatusFor(DailyReportMetrics.Reach)
            },
            new()
            {
                Metric = "Video Views",
                Definition = "Agregasi harian Video Views level akun.",
                Status = StatusFor(DailyReportMetrics.VideoViews)
            },
            new()
            {
                Metric = "Profile Views",
                Definition = "Agregasi harian Profile Views level akun.",
                Status = StatusFor(DailyReportMetrics.ProfileViews)
            },
            new()
            {
                Metric = "Interactions",
                Definition = "Likes + Comments + Shares, dihitung sistem dari field terpisah video list API " +
                    "(analytics/202605) untuk seluruh periode aktif - total periode tidak pernah didistribusikan " +
                    "rata-rata ke tanggal; detail harian ditampilkan jika tersedia interval harian.",
                Status = StatusFor(DailyReportMetrics.Interactions)
            },
            new()
            {
                Metric = "Engagement Rate",
                Definition = "Interactions / Reach x 100% - Reach sebagai pembagi (Winning Content tetap memakai Views). " +
                    "Jika Reach belum tersedia, nilai tetap \"-\" dengan alasan tersebut.",
                Status = "dihitung oleh dashboard"
            },
            new()
            {
                Metric = "Net Follower",
                Definition = "Followers Gained - Followers Lost (harian, kemudian diakumulasi per periode).",
                Status = StatusFor(DailyReportMetrics.NetFollower)
            },
            new()
            {
                Metric = "Total Follower",
                Definition = "Follower count pada posisi akhir Current Period dari snapshot historis valid - tidak pernah memakai data setelah EndDate.",
                Status = StatusFor(DailyReportMetrics.TotalFollower)
            },
            new()
            {
                Metric = "Product Impressions",
                Definition = "Jumlah tampilan produk (product_impressions) dari interval harian TikTok Shop Video Performance Overview - bukan metric engagement profile.",
                Status = StatusFor(DailyReportMetrics.ProductImpressions)
            },
            new()
            {
                Metric = "Product Clicks",
                Definition = "Jumlah klik produk (product_clicks) dari interval harian TikTok Shop Video Performance Overview - bukan metric engagement profile.",
                Status = StatusFor(DailyReportMetrics.ProductClicks)
            },
            new()
            {
                Metric = "Video-attributed SKU Orders",
                Definition = "Jumlah pesanan SKU yang dikaitkan ke video (video_attributed_sku_orders) dari interval harian TikTok Shop Video Performance Overview - bukan metric engagement profile.",
                Status = StatusFor(DailyReportMetrics.VideoAttributedSkuOrders)
            },
            new()
            {
                Metric = "Video-attributed GMV",
                Definition = "GMV yang dikaitkan langsung ke video (video_attributed_gmv) per hari, diakumulasi per periode dengan mata uang yang sama - tidak pernah digabung dengan subtype GMV lain. Terverifikasi: video_attributed_gmv = video_gmv + video_indirect_gmv.",
                Status = StatusFor(DailyReportMetrics.VideoAttributedGmv)
            },
            new()
            {
                Metric = "Video GMV",
                Definition = "GMV langsung dari video (video_gmv, subtype dari video_attributed_gmv) per hari - dipisah dari atribusi tidak langsung.",
                Status = StatusFor(DailyReportMetrics.VideoGmv)
            },
            new()
            {
                Metric = "Video Indirect GMV",
                Definition = "GMV tidak langsung yang dikaitkan ke video (video_indirect_gmv) per hari - subtype terpisah, tidak dijumlahkan dengan Video GMV.",
                Status = StatusFor(DailyReportMetrics.VideoIndirectGmv)
            },
            new()
            {
                Metric = "GPM",
                Definition = "Video-attributed GMV / Video Views x 1000 (field gpm) - definisi terverifikasi terhadap response live, dengan mata uang yang sama seperti GMV.",
                Status = StatusFor(DailyReportMetrics.Gpm)
            },
            new()
            {
                Metric = "Product CTR",
                Definition = "Product Clicks / Product Impressions x 100% (field ctr overview - terverifikasi terhadap response live; dihitung ulang dari total periode, bukan rata-rata harian).",
                Status = StatusFor(DailyReportMetrics.ProductCtr)
            },
            new()
            {
                Metric = "CTOR SKU Order",
                Definition = "Video-attributed SKU Orders / Product Clicks x 100% (field ctor_sku_order) - dihitung ulang dari total periode, bukan rata-rata harian.",
                Status = StatusFor(DailyReportMetrics.CtorSkuOrder)
            },
            new()
            {
                Metric = "Video Product Viewers",
                Definition = "Jumlah pemirsa produk dari video (video_product_viewers) per hari - akumulasi nilai harian (pemirsa unik antar hari bisa tumpang tindih), bukan unique viewer periode.",
                Status = StatusFor(DailyReportMetrics.VideoProductViewers)
            },
            new()
            {
                Metric = "Avg Daily Customers",
                Definition = "Rata-rata pelanggan baru per hari dari video (avg_daily_customers) - periode memakai rata-rata hari yang memiliki record, bukan jumlah akumulasi.",
                Status = StatusFor(DailyReportMetrics.AvgDailyCustomers)
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
