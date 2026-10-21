using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KaleContentOps.Services.CreativePerformance;

// ============================================================================
// Account-level daily metrics SOURCE CONTRACT.
//
// WHY THIS EXISTS (audit finding, Creative Performance - Daily Report):
// The only ingested metrics in this application are PER-VIDEO
// (ContentMetric -> ContentLog), fetched from the TikTok video performance
// endpoints (/analytics/202509/shop_videos/{id}/performance and
// /analytics/202605/shop_videos/performance). Those endpoints never return
// account-level analytics. There is NO table and NO endpoint in this workspace
// that provides daily account-level Reach/Total Viewers, Video Views, Profile
// Views, Followers Lost, Total Follower or new-vs-returning viewers.
//
// Summing per-video Reach to fake an account Reach is explicitly forbidden by
// the spec (per-video != account-level), so the Daily Report reads from this
// abstraction instead of inventing numbers. The only implementation shipped
// today is <see cref="UnavailableAccountDailyMetricSource"/> (status: BLOCKED);
// when a verified account-level provider exists, implement this interface over
// it (EF Core table or TikTok account analytics endpoint) and swap the DI
// registration - the whole dashboard starts working with no UI change.
//
// ---------------------------------------------------------------------------
// API CANDIDATE INVESTIGATION (2026-10-21, documentation-only finding - NO
// mapping was implemented because nothing was verified at FIELD level):
//
// A) GET /analytics/202609/shop/performance ("Get Shop Performance", version
//    202609). Verified from the indexed official doc page
//    (partner.tiktokshop.com/docv2/page/get-shop-performance-202609): required
//    scope TikTok Shop Analytics (data.shop_analytics.public.read), described
//    as "Returns performance metrics at shop/seller level". The response field
//    list could NOT be retrieved in this workspace (the doc host times out from
//    here and no archived snapshot of the 202609 page exists), so it is
//    UNVERIFIED whether it carries Reach/Total Viewers, Video Views, Profile
//    Views, Likes/Comments/Shares, Followers Gained/Lost, follower snapshots or
//    new-vs-returning viewers, and at which granularity.
//
// B) GET /analytics/202609/shop_videos/overview_performance ("Get Shop Video
//    Performance Overview", version 202609). Same required scope
//    (data.shop_analytics.public.read); described as "Returns overall
//    performance metrics for all videos under a shop".
//
//    VERIFIED 2026-10-21 via the official API Testing Tool (code=0, Success):
//    with granularity=1D, currency=LOCAL, account_type=ALL, today=false and a
//    start_date_ge/end_date_lt window, the response (data.latest_available_date
//    + daily intervals with start_date, end_date, video_views) supplies REAL
//    DAILY video_views - mapped to <see cref="DailyReportMetrics.VideoViews"/>
//    by TikTokShopVideoOverviewMetricSource.
//
//    The SAME verified daily-interval object also carries commerce-funnel and
//    audience metadata fields at the record level (field presence verified against
//    the live response shape used by this source): product_impressions,
//    product_clicks, video_attributed_sku_orders, the GMV subtypes
//    (video_attributed_gmv.amount, shoppable_video_attributed_gmv.amount,
//    video_gmv.amount, video_indirect_gmv.amount), ctr, ctor_sku_order, gpm.amount
//    (with currency), avg_daily_customers and video_product_viewers. Those fields are
//    read and stored on <see cref="AccountDailyMetricRecord"/> as READ-ONLY daily
//    metadata. They are NOT mapped to Reach / Profile Views / Interactions /
//    Engagement Rate / Net Follower / Total Follower: they have a different meaning
//    (per-day commerce funnel and audience data), they must never be summed or averaged
//    across days (percentages/averages), and GMV subtypes must never be added together
//    into a single GMV figure. video_product_viewers is NEVER used as Reach.
//
//    The three commerce-funnel fields are now surfaced on their OWN dashboard cards
//    and daily rows (Product Impressions, Product Clicks, Video-attributed SKU Orders)
//    because the TikTok Shop Video Performance Overview endpoint is the verified
//    account-level source for them. They remain classified as READ-ONLY record metadata
//    in the sense that they are NOT Reach/Interactions/Engagement Rate/Follower metrics
//    and must never be mixed into those families; the distinction is documented at
//    <see cref="TikTokShopVideoOverviewMetricSource"/>.
//
// VERIFICATION LIMIT (documentation): this environment has no outbound network
// egress, so the live probe above was executed by the shop operator through the
// official API Testing Tool - not from this workspace. The remaining endpoints
// for account-level Reach/Profile Views/followers are still unverified here.
// ---------------------------------------------------------------------------
// ============================================================================

/// <summary>Half-open [Start, EndExclusive) query window in shop-local calendar dates.</summary>
public sealed class AccountDailyMetricQuery
{
    public DateTime Start { get; init; }

    public DateTime EndExclusive { get; init; }
}

/// <summary>
/// One day of account-level metrics. Null means "no value" and is NEVER converted
/// to 0 downstream. These are account totals for that calendar day (e.g. the day's
/// Total Viewers), not sums of per-video values.
/// </summary>
public sealed class AccountDailyMetricRecord
{
    public DateTime Date { get; init; }

    /// <summary>Account-level Reach / Total Viewers for the day.</summary>
    public long? Reach { get; init; }

    public long? VideoViews { get; init; }

    public long? ProfileViews { get; init; }

    public long? Likes { get; init; }

    public long? Comments { get; init; }

    public long? Shares { get; init; }

    public long? FollowersGained { get; init; }

    /// <summary>Unfollows. NOT available from the current per-video integration.</summary>
    public long? FollowersLost { get; init; }

    /// <summary>
    /// TikTok Shop Video Performance Overview extra fields (verified present on the
    /// daily interval object at the field level against the live response shape used
    /// by TikTokShopVideoOverviewMetricSource). These are READ-ONLY record metadata
    /// attached to one daily interval. They are NOT Reach, NOT Interactions, NOT
    /// Engagement Rate, NOT Net/Total Follower - they live on the record because that
    /// is where TikTok puts them, not because they are mapped into those summary families.
    /// Null != 0: an absent field on a given day stays null.
    /// Percentages and averages (ctr, ctor_sku_order, gpm, avg_daily_customers) are
    /// stored as read-only metadata only - never summed or averaged across days.
    /// GMV subtypes are stored separately and are NEVER summed into a single GMV figure.
    /// </summary>
    public long? ProductImpressions { get; init; }

    public long? ProductClicks { get; init; }

    public long? VideoAttributedSkuOrders { get; init; }

    /// <summary>
    /// video_attributed_gmv.amount from the daily interval. Currency is tracked on the
    /// record (VideoAttributedGmvCurrency) because a GMV amount carries its currency and
    /// must not be combined with another currency blindly.
    /// </summary>
    public decimal? VideoAttributedGmvAmount { get; init; }

    public string? VideoAttributedGmvCurrency { get; init; }

    public decimal? ShoppableVideoAttributedGmvAmount { get; init; }

    public string? ShoppableVideoAttributedGmvCurrency { get; init; }

    public decimal? VideoGmvAmount { get; init; }

    public string? VideoGmvCurrency { get; init; }

    public decimal? VideoIndirectGmvAmount { get; init; }

    public string? VideoIndirectGmvCurrency { get; init; }

    /// <summary>ctr as a decimal fraction (e.g. 0.0545), stored read-only.</summary>
    public decimal? Ctr { get; init; }

    /// <summary>ctor_sku_order as a decimal fraction, stored read-only.</summary>
    public decimal? CtorSkuOrder { get; init; }

    /// <summary>gpm.amount as a decimal, stored read-only. Currency tracked separately.</summary>
    public decimal? GpmAmount { get; init; }

    public string? GpmCurrency { get; init; }

    public decimal? AvgDailyCustomers { get; init; }

    public long? VideoProductViewers { get; init; }
}

/// <summary>
/// Historical follower-count snapshot. The Daily Report only ever uses a snapshot
/// dated inside the selected Current Period (never a snapshot taken after EndDate).
/// </summary>
public sealed class FollowerSnapshot
{
    public DateTime AsOfDate { get; init; }

    public long FollowerCount { get; init; }

    /// <summary>When the snapshot was captured (UTC), when the provider records it.</summary>
    public DateTime? CapturedAt { get; init; }
}

/// <summary>
/// Period-level (NOT daily) audience split. Unique viewers cannot be summed across
/// days without double counting, so the source itself must supply the period totals.
/// </summary>
public sealed class AccountViewerComposition
{
    public long? NewViewers { get; init; }

    public long? ReturningViewers { get; init; }

    /// <summary>Source definition note, e.g. "unique viewers for the whole queried period".</summary>
    public string? Definition { get; init; }
}

/// <summary>
/// Verified TikTok shop video metric keys used by <see cref="TikTokShopVideoOverviewMetricSource"/>
/// for the per-record (daily interval) fields. These are NOT dashboard summary-metric keys
/// (<see cref="DailyReportMetrics"/>). They exist so callers can inspect what this source
/// actually populates without parsing the TikTok response shape.
/// </summary>
public static class TikTokShopVideoOverviewMetricSourceMetrics
{
    // read-only daily record metadata (NOT dashboard summary metrics)
    public const string VideoViews = "video_views";
    public const string ProductImpressions = "product_impressions";
    public const string ProductClicks = "product_clicks";
    public const string VideoAttributedSkuOrders = "video_attributed_sku_orders";
    public const string VideoAttributedGmv = "video_attributed_gmv";
    public const string ShoppableVideoAttributedGmv = "shoppable_video_attributed_gmv";
    public const string VideoGmv = "video_gmv";
    public const string VideoIndirectGmv = "video_indirect_gmv";

    // read-only percentages/fractions (never summed or averaged)
    public const string Ctr = "ctr";
    public const string CtorSkuOrder = "ctor_sku_order";
    public const string Gpm = "gpm";

    // read-only audience/composite metadata
    public const string AvgDailyCustomers = "avg_daily_customers";
    public const string VideoProductViewers = "video_product_viewers";
}

/// <summary>A verified account-level dataset for exactly one queried period.</summary>
public sealed class AccountDailyMetricDataset
{
    public IReadOnlyList<AccountDailyMetricRecord> Records { get; init; }
        = Array.Empty<AccountDailyMetricRecord>();

    public IReadOnlyList<FollowerSnapshot> FollowerSnapshots { get; init; }
        = Array.Empty<FollowerSnapshot>();

    public AccountViewerComposition? ViewerComposition { get; init; }

    /// <summary>Human-readable provider name for the footer ("sumber data").</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>
    /// Metric keys (<see cref="DailyReportMetrics"/>.*) this dataset actually supplies
    /// for the seven DASHBOARD summary cards (Reach, Video Views, Profile Views,
    /// Interactions, Engagement Rate, Net Follower, Total Follower).
    /// Null = every dashboard metric is supplied (backward compatible with full providers).
    /// A provider backed by a VERIFIED PARTIAL source MUST declare exactly its verified
    /// dashboard keys: the service then renders every other dashboard metric as Unavailable
    /// ("-") with an honest reason, instead of showing "no data recorded" or - worse -
    /// letting a value from an unrelated field leak into a metric it does not measure.
    ///
    /// NOTE: TikTok Shop Video Performance Overview extra record fields
    /// (ProductImpressions, ProductClicks, VideoAttributedSkuOrders) ARE dashboard
    /// metrics of their own and are declared here, because that endpoint is the
    /// verified source for them. They still are NOT Reach, NOT Interactions, NOT
    /// Engagement Rate and NOT follower counts: they form the shop-video commerce
    /// funnel and are rendered in their own section, never inside the
    /// account-engagement families. The remaining extra fields (the GMV subtypes,
    /// ctr, ctor_sku_order, gpm, avg_daily_customers, video_product_viewers) stay
    /// read-only record metadata: they are per-day values that must never be summed
    /// or averaged across days, so they are intentionally NOT added to this set.
    /// </summary>
    public IReadOnlySet<string>? AvailableMetricKeys { get; init; }
}

/// <summary>Result of asking the provider for one period.</summary>
public sealed class AccountDailyMetricResult
{
    public bool IsAvailable { get; init; }

    /// <summary>Why the metric family is unavailable (null when IsAvailable).</summary>
    public string? UnavailableReason { get; init; }

    public AccountDailyMetricDataset? Dataset { get; init; }

    public static AccountDailyMetricResult Available(AccountDailyMetricDataset dataset)
        => new() { IsAvailable = true, Dataset = dataset };

    public static AccountDailyMetricResult Unavailable(string reason)
        => new() { IsAvailable = false, UnavailableReason = reason };
}

/// <summary>Provider of daily ACCOUNT-LEVEL analytics (see contract notes above).</summary>
public interface IAccountDailyMetricSource
{
    Task<AccountDailyMetricResult> GetAsync(AccountDailyMetricQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The only provider registered today: no verified account-level source exists in
/// this workspace, so it reports Unavailable with the audit reason. This is what
/// makes every Daily Report metric render "-" honestly instead of fabricated
/// per-video aggregates. BLOCKED until an account-level integration is verified.
/// </summary>
public sealed class UnavailableAccountDailyMetricSource : IAccountDailyMetricSource
{
    public const string Reason =
        "Sumber analytics level akun (harian) belum terintegrasi. Endpoint yang tersedia saat ini " +
        "hanya mengembalikan metrik per-video (ContentMetric), yang bukan account-level analytics " +
        "(Reach/Total Viewers akun, Video Views akun, Profile Views, Followers Lost, Total Follower " +
        "snapshot, dan viewer baru/lama). Metrik tidak ditampilkan sampai sumber account-level diverifikasi.";

    public Task<AccountDailyMetricResult> GetAsync(AccountDailyMetricQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(AccountDailyMetricResult.Unavailable(Reason));
}
