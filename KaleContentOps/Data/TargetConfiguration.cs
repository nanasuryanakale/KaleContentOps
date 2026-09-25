using KaleContentOps.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KaleContentOps.Data;

/// <summary>
/// Persistence configuration for Target (Menu Targets, Phase 1).
/// Business rules (only NON_KK / KK may have targets, same-day edit vs new-day versioning)
/// are enforced by the service layer in a later phase - the database only guarantees
/// structural integrity: FK to ContentTypes, non-negative targets, sane date range,
/// and at most one active version per content type.
/// </summary>
public class TargetConfiguration : IEntityTypeConfiguration<Target>
{
    public void Configure(EntityTypeBuilder<Target> builder)
    {
        builder.ToTable("Targets", tb =>
        {
            tb.HasCheckConstraint("CK_Targets_TargetUpload_NonNegative", "[TargetUpload] >= 0");
            tb.HasCheckConstraint("CK_Targets_TargetViews_NonNegative", "[TargetViews] >= 0");
            tb.HasCheckConstraint("CK_Targets_EffectiveDates", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");
        });

        builder.HasKey(x => x.Id);

        // FK to ContentTypes. Existing master-data relationships use DeleteBehavior.SetNull,
        // but that requires a nullable FK; Target.ContentTypeId is required (non-nullable),
        // so Restrict is the consistent non-destructive choice.
        builder.HasOne(x => x.ContentType)
            .WithMany()
            .HasForeignKey(x => x.ContentTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.TargetUpload)
            .IsRequired();

        builder.Property(x => x.TargetViews)
            .IsRequired();

        builder.Property(x => x.EffectiveFrom)
            .IsRequired()
            .HasColumnType("date");

        builder.Property(x => x.EffectiveTo)
            .HasColumnType("date");

        // Audit (Phase 4b): stable Identity user id of the last value change.
        // Nullable - target rows created before this column existed stay valid.
        builder.Property(x => x.ChangedByUserId)
            .HasMaxLength(450);

        builder.HasOne(x => x.ChangedBy)
            .WithMany()
            .HasForeignKey(x => x.ChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict); // audit rows must survive user removal

        // One version per content type per effective date: a same-date save must
        // revise the existing version (service-layer rule), so duplicate EffectiveFrom
        // rows would be ambiguous for date-based historical lookup. Backed by the
        // covering lookup index below; this one enforces the invariant.
        builder.HasIndex(x => new { x.ContentTypeId, x.EffectiveFrom })
            .IsUnique()
            .HasDatabaseName("IX_Targets_ContentTypeId_EffectiveFrom_Unique");

        // At most one active (unclosed) target version per content type
        builder.HasIndex(x => x.ContentTypeId)
            .IsUnique()
            .HasFilter("[EffectiveTo] IS NULL")
            .HasDatabaseName("IX_Targets_ContentTypeId_Active");

        // Historical lookup by report date: WHERE ContentTypeId = ? AND EffectiveFrom <= d AND (EffectiveTo IS NULL OR EffectiveTo >= d)
        // INCLUDE makes lookups covering (no key lookup for TargetUpload/TargetViews).
        builder.HasIndex(x => new { x.ContentTypeId, x.EffectiveFrom, x.EffectiveTo })
            .IncludeProperties(x => new { x.TargetUpload, x.TargetViews })
            .HasDatabaseName("IX_Targets_ContentTypeId_EffectiveFrom_EffectiveTo");
    }
}
