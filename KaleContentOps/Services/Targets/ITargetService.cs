using KaleContentOps.Models;

namespace KaleContentOps.Services.Targets;

/// <summary>
/// Domain service for Menu Targets persistence: versioned weekly targets per content type.
/// Only NON_KK and KK may have targets (validated by ContentType.Code, never by integer id);
/// AUTO_GMV_LIVE and unknown codes are rejected.
/// </summary>
public interface ITargetService
{
    /// <summary>
    /// Target version effective today for NON_KK and KK with content type code + name,
    /// ordered NON_KK first. AUTO_GMV_LIVE never appears. A scheduled future version does
    /// NOT become "current" before its EffectiveFrom date - today's version wins
    /// (date-based resolution, never "latest row wins").
    /// </summary>
    Task<IReadOnlyList<TargetCurrentItem>> GetCurrentTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Scheduled (future) target versions: EffectiveFrom &gt; today, still inactive.
    /// Same shape as current items so the UI can render them consistently.
    /// </summary>
    Task<IReadOnlyList<TargetCurrentItem>> GetScheduledTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Target version effective on a report date for one content type
    /// (EffectiveFrom &lt;= reportDate &amp;&amp; (EffectiveTo == null || EffectiveTo &gt;= reportDate)).
    /// </summary>
    Task<Target?> GetTargetForDateAsync(int contentTypeId, DateOnly reportDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persisted target versions for one content type, EffectiveDate descending (newest first).
    /// Phase 5 readiness: historical calculations resolve targets via
    /// <see cref="GetTargetForDateAsync"/>, never via the current/latest version.
    /// </summary>
    Task<IReadOnlyList<TargetHistoryItem>> GetTargetHistoryAsync(int contentTypeId, int maxRows = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the payload and persists the versioning outcome:
    /// - save with EffectiveDate == an existing version's date: that version is REVISED in place (no duplicate date);
    /// - save with a new (past or today) date: the versions overlapping the new date are trimmed
    ///   (re-opened/closed) and the new version becomes effective from that date;
    /// - save with a future date: a SCHEDULED version is stored; the version covering today stays active
    ///   until the scheduled date arrives (no active-row anomaly);
    /// - no versions at all: creates the first one.
    /// ChangedByUserId/ChangedAt are stamped on the version whose values were last written.
    /// </summary>
    Task<TargetSaveResult> SaveTargetAsync(TargetSaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Actual counts and views for Menu Targets UI (rolling N days from endDate).
    /// Aggregates ContentLogs posted in [endDate - days + 1, endDate] for one content type,
    /// retrieves latest metrics per log, and sums views + counts uploads.
    /// Returns null if content type not found or no data in range.
    /// Used by Menu Targets to display rolling 7-day actuals alongside targets (not for versioning).
    /// </summary>
    Task<TargetActualItem?> GetActualAsync(int contentTypeId, DateOnly endDate, int days = 7, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 5 read model for the Menu Targets page: per targetable content type (NON_KK, KK,
    /// NON_KK first), the target effective on endDate resolved by date (never "latest row wins"),
    /// the rolling N-day actuals (same pipeline as GetActualAsync), and the derived selisih
    /// (actual - target, negatives kept) plus the combined status. Missing target -> zeros
    /// (no target configured yet), so the UI can still show actuals.
    /// Set-based: two bulk queries total (content logs + all latest metrics), never per-log lookups.
    /// </summary>
    Task<TargetActualsSummary> GetActualsSummaryAsync(DateOnly endDate, int days = 7, CancellationToken cancellationToken = default);
}

/// <summary>
/// Combined Status rule (Phase 5): <c>Tercapai</c> only when ActualUpload &gt;= TargetUpload
/// AND ActualViews &gt;= TargetViews; otherwise <c>Belum Tercapai</c>. The rule is applied
/// literally: with no configured target (zeros) and zero actuals, 0 &gt;= 0 holds on both
/// metrics -> Tercapai, matching the existing per-metric convention (selisih 0 = "Mencapai
/// target") in the Phase 3 UI.
/// </summary>
public static class TargetActualStatus
{
    public const string Achieved = "Tercapai";
    public const string NotAchieved = "Belum Tercapai";

    public static string Resolve(int actualUpload, int targetUpload, long actualViews, long targetViews) =>
        actualUpload >= targetUpload && actualViews >= targetViews ? Achieved : NotAchieved;
}

/// <summary>
/// Phase 5 read model: rolling N-day actuals summary for the Menu Targets page.
/// StartDate is inclusive, EndDate is inclusive (exactly N calendar days).
/// </summary>
public sealed class TargetActualsSummary
{
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public int Days { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;
    public IReadOnlyList<TargetActualItem> Items { get; init; } = Array.Empty<TargetActualItem>();
}

/// <summary>
/// Read model for the target version effective today (API/UI facing).
/// </summary>
public sealed class TargetCurrentItem
{
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;
    public string ContentTypeName { get; init; } = string.Empty;
    public int TargetUpload { get; init; }
    public long TargetViews { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }

    /// <summary>Stable Identity user id of the last value change (null for pre-audit rows).</summary>
    public string? ChangedByUserId { get; init; }

    /// <summary>Display name resolved server-side for UI display (never from request body).</summary>
    public string? ChangedByName { get; init; }

    /// <summary>When the values of this version were last written (UtcNow).</summary>
    public DateTime? ChangedAt { get; init; }
}

/// <summary>
/// Actual aggregated data for Menu Targets UI (rolling period actuals, not target configuration).
/// Represents ContentLogs posted in the rolling N-day period, with latest metrics.
/// Phase 5: carries the date-resolved target for the same period end date plus the derived
/// selisih (actual - target, negatives preserved) and the combined Tercapai/Belum Tercapai status.
/// </summary>
public sealed class TargetActualItem
{
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;
    public string ContentTypeName { get; init; } = string.Empty;
    /// <summary>Count of ContentLogs in the rolling period.</summary>
    public int ActualUpload { get; init; }
    /// <summary>Sum of latest Views for each ContentLog in the rolling period.</summary>
    public long ActualViews { get; init; }

    /// <summary>Target version effective on the period end date (zeros when none is configured yet).</summary>
    public int TargetUpload { get; init; }
    public long TargetViews { get; init; }
    /// <summary>EffectiveFrom of the resolved target version (null when no target is configured yet).</summary>
    public DateOnly? TargetEffectiveFrom { get; init; }

    /// <summary>ActualUpload - TargetUpload (negative when below target; never absolute).</summary>
    public int SelisihUpload => ActualUpload - TargetUpload;

    /// <summary>ActualViews - TargetViews (negative when below target; never absolute).</summary>
    public long SelisihViews => ActualViews - TargetViews;

    /// <summary>Combined Tercapai/Belum Tercapai status per the Phase 5 rule.</summary>
    public string Status => TargetActualStatus.Resolve(ActualUpload, TargetUpload, ActualViews, TargetViews);
}

/// <summary>
/// One row of the target change history (Riwayat Perubahan Target):
/// version EffectiveFrom, values, and who changed them. Ordered EffectiveFrom descending.
/// </summary>
public sealed class TargetHistoryItem
{
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;
    public DateOnly EffectiveDate { get; init; }
    public DateOnly? EffectiveUntil { get; init; }
    public int TargetUpload { get; init; }
    public long TargetViews { get; init; }
    public string? ChangedByUserId { get; init; }
    public string? ChangedByName { get; init; }
    public DateTime? ChangedAt { get; init; }
    /// <summary>True when this version is scheduled (EffectiveFrom in the future, not yet active).</summary>
    public bool IsScheduled { get; init; }
    /// <summary>True when this version is the one effective today.</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>
/// Save request. Today is injected so tests are independent of the system clock;
/// EffectiveDate is the user-provided "Berlaku Mulai" (defaults to Today when absent,
/// preserving the legacy same-day auto-save behavior). ChangedByUserId is resolved
/// server-side from ICurrentUser by the controller - never from the request body.
/// </summary>
public sealed class TargetSaveRequest
{
    public int ContentTypeId { get; set; }
    public int TargetUpload { get; set; }
    public long TargetViews { get; set; }

    /// <summary>Application date used for versioning decisions (injectable for tests).</summary>
    public DateOnly Today { get; set; }

    /// <summary>
    /// "Berlaku Mulai" for the save. Null/absent keeps the legacy behavior (Today).
    /// May be past (backdate: historical period keeps the older version before it),
    /// today (same-day revise rule), or future (scheduled version).
    /// </summary>
    public DateOnly? EffectiveDate { get; set; }

    /// <summary>Stable Identity user id of the authenticated user (server-side only).</summary>
    public string? ChangedByUserId { get; set; }
}

/// <summary>
/// Outcome of <see cref="ITargetService.SaveTargetAsync"/>.
/// Success == false means the payload was rejected (validation), never a store failure —
/// infrastructure errors propagate as exceptions, matching the existing application style.
/// </summary>
public sealed class TargetSaveResult
{
    public bool Success { get; init; }

    /// <summary>Validation error code; null on success.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable message (UI can show it directly in a later phase).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The persisted version (new or updated) when Success is true.</summary>
    public Target? Target { get; init; }

    /// <summary>Code of the target content type (from the validated ContentType row).</summary>
    public string? ContentTypeCode { get; init; }

    /// <summary>True when SaveTargetAsync created a new version instead of revising an existing date.</summary>
    public bool CreatedNewVersion { get; init; }

    /// <summary>True when the persisted version is scheduled (EffectiveFrom in the future).</summary>
    public bool IsScheduled { get; init; }

    public static TargetSaveResult Ok(Target target, string contentTypeCode, bool createdNewVersion, bool isScheduled = false) =>
        new() { Success = true, Target = target, ContentTypeCode = contentTypeCode, CreatedNewVersion = createdNewVersion, IsScheduled = isScheduled };

    public static TargetSaveResult Fail(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}

/// <summary>Stable validation error codes returned by <see cref="ITargetService.SaveTargetAsync"/>.</summary>
public static class TargetSaveErrorCodes
{
    public const string TargetUploadNegative = "TARGET_UPLOAD_NEGATIVE";
    public const string TargetViewsNegative = "TARGET_VIEWS_NEGATIVE";
    public const string ContentTypeNotFound = "CONTENT_TYPE_NOT_FOUND";
    public const string ContentTypeNotTargetable = "CONTENT_TYPE_NOT_TARGETABLE";
    public const string ActiveVersionAnomaly = "ACTIVE_VERSION_ANOMALY";
    public const string EffectiveDateBeforeFirst = "EFFECTIVE_DATE_BEFORE_FIRST";
}
