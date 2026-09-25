using System;
using KaleContentOps.Services.Targets;

namespace KaleContentOps.ViewModels;

/// <summary>
/// Phase 3 save-target request payload (Menu Targets auto-save) + Phase 4b Effective Date.
/// Contains NO Today / CreatedAt / UpdatedAt / ChangedByUserId: "today" comes from the
/// server clock (IShopTimeZone) and the audit user comes from ICurrentUser - the request
/// body can never influence either. EffectiveDate is the user's "Berlaku Mulai";
/// absent keeps the legacy behavior (effective from server today).
/// </summary>
public sealed class TargetSaveDto
{
    public int ContentTypeId { get; set; }
    public int TargetUpload { get; set; }
    public long TargetViews { get; set; }

    /// <summary>"Berlaku Mulai" (inclusive). ISO date (yyyy-MM-dd) via JSON binding.</summary>
    public DateOnly? EffectiveDate { get; set; }
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

    /// <summary>True when the persisted version is scheduled (Effective Date in the future).</summary>
    public bool IsScheduled { get; init; }
}
