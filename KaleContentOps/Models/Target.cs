namespace KaleContentOps.Models;

/// <summary>
/// Weekly content target version for one content type (NON_KK / KK).
/// Historical versioning: editing targets on a new date closes the previous version
/// (EffectiveTo = day before the new EffectiveFrom) and creates a new row; same-day
/// edits update the row whose EffectiveFrom equals that day.
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

    public ContentType? ContentType { get; set; }
}
