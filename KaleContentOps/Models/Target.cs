namespace KaleContentOps.Models;

/// <summary>
/// Weekly content target version for one content type (NON_KK / KK).
/// Historical versioning: editing targets on a new date closes the previous version
/// (EffectiveTo = day before the new EffectiveFrom) and creates a new row; same-day
/// edits update the row whose EffectiveFrom equals that day.
/// Scheduled (future) versions are stored with EffectiveFrom in the future; the version
/// covering today stays active until that date arrives (date-based resolution, never
/// "latest row wins").
/// AUTO_GMV_LIVE has no target - enforcement lives in the service layer (Phase 2).
/// </summary>
public class Target
{
    public int Id { get; set; }

    public int ContentTypeId { get; set; }

    // Target uploads per week (>= 0, enforced by check constraint)
    public int TargetUpload { get; set; }

    // Target views per week (>= 0, enforced by check constraint)
    public long TargetViews { get; set; }

    // Inclusive first calendar date this version applies to
    public DateOnly EffectiveFrom { get; set; }

    // Inclusive last calendar date this version applies to; null = currently active version
    public DateOnly? EffectiveTo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ------------------------------------------------------------------
    // Audit (Phase 4b): who last set the target VALUES of this version.
    // Stable ASP.NET Core Identity user id (AspNetUsers.Id), resolved
    // server-side from ICurrentUser - never taken from the request body.
    // Nullable: rows created before this column existed.
    // Closing a version (EffectiveTo set by a later save) does NOT rewrite
    // these fields - they describe the last content change of THIS version.
    // ------------------------------------------------------------------
    public string? ChangedByUserId { get; set; }

    public ApplicationUser? ChangedBy { get; set; }

    public ContentType? ContentType { get; set; }
}
