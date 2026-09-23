using KaleContentOps.Services.Targets;

namespace KaleContentOps.ViewModels;

/// <summary>
/// Phase 3 save-target request payload (Menu Targets auto-save).
/// Deliberately contains NO EffectiveFrom / EffectiveTo / Today / CreatedAt / UpdatedAt:
/// the effective date is decided by the service (TargetService) from the server clock.
/// </summary>
public sealed class TargetSaveDto
{
    public int ContentTypeId { get; set; }
    public int TargetUpload { get; set; }
    public long TargetViews { get; set; }
}

/// <summary>
/// Final persisted state returned to the frontend after a successful save,
/// so the UI can render the stored version without re-fetching.
/// </summary>
public sealed class TargetSaveResponseDto
{
    public bool Success { get; init; }
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;
    public int TargetUpload { get; init; }
    public long TargetViews { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public bool CreatedNewVersion { get; init; }
}
