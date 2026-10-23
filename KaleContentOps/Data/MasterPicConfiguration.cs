using KaleContentOps.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KaleContentOps.Data;

/// <summary>
/// Persistence configuration for MasterPic (employment period tracking).
/// Enforces business rules: JoinDate must not be NULL, ResignDate >= JoinDate when set.
/// </summary>
public class MasterPicConfiguration : IEntityTypeConfiguration<MasterPic>
{
    public void Configure(EntityTypeBuilder<MasterPic> builder)
    {
        builder.ToTable("MasterPics", tb =>
        {
            // Business rule: ResignDate must be >= JoinDate if set
            tb.HasCheckConstraint("CK_MasterPics_ResignDateGteJoinDate",
                "[ResignDate] IS NULL OR [ResignDate] >= [JoinDate]");
        });

        builder.HasKey(x => x.Id);

        // JoinDate and ResignDate are business calendar dates (IANA DATE type)
        builder.Property(x => x.JoinDate)
            .HasColumnType("date")
            .IsRequired();

        builder.Property(x => x.ResignDate)
            .HasColumnType("date");
    }
}
