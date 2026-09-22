using KaleContentOps.Models;

namespace KaleContentOps.Services.Targets;

/// <summary>
/// Domain service for Menu Targets persistence: versioned weekly targets per content type.
/// Only NON_KK and KK may have targets (validated by ContentType.Code, never by integer id);
/// AUTO_GMV_LIVE and unknown codes are rejected.
/// </summary>
public interface ITargetService
{
    /// <summary>Active (EffectiveTo == null) targets for NON_KK and KK, keyed by content type code.</summary>
    Task<IReadOnlyDictionary<string, Target>> GetCurrentTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Target version effective on a report date for one content type
    /// (EffectiveFrom &lt;= reportDate &amp;&amp; (EffectiveTo == null || EffectiveTo &gt;= reportDate)).
    /// </summary>
    Task<Target?> GetTargetForDateAsync(int contentTypeId, DateOnly reportDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the payload and persists the versioning outcome:
    /// same-day edit updates the version whose EffectiveFrom == today;
    /// a new-day edit closes the previous active version (EffectiveTo = today - 1) and creates a new active version;
    /// no active version simply creates the first one.
    /// </summary>
    Task<TargetSaveResult> SaveTargetAsync(TargetSaveRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Save request. Today is injected so tests are independent of the system clock.</summary>
public sealed class TargetSaveRequest
{
    public int ContentTypeId { get; set; }
    public int TargetUpload { get; set; }
    public long TargetViews { get; set; }

    /// <summary>Application date used for versioning decisions (injectable for tests).</summary>
    public DateOnly Today { get; set; }
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

    /// <summary>True when SaveTargetAsync created a new version instead of updating the same-day one.</summary>
    public bool CreatedNewVersion { get; init; }

    public static TargetSaveResult Ok(Target target, bool createdNewVersion) =>
        new() { Success = true, Target = target, CreatedNewVersion = createdNewVersion };

    public static TargetSaveResult Fail(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
