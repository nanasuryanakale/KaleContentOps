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
