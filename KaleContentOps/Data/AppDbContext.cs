using KaleContentOps.Models;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<ContentLog> ContentLogs => Set<ContentLog>();
    public DbSet<ContentMetric> ContentMetrics => Set<ContentMetric>();
    public DbSet<MasterPic> MasterPics => Set<MasterPic>();
    public DbSet<ContentType> ContentTypes => Set<ContentType>();
    public DbSet<ProductionMethod> ProductionMethods => Set<ProductionMethod>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ContentLog
        modelBuilder.Entity<ContentLog>()
            .HasIndex(x => x.VideoId)
            .IsUnique();

        modelBuilder.Entity<ContentLog>()
            .HasOne(x => x.ContentType)
            .WithMany(x => x.ContentLogs)
            .HasForeignKey(x => x.ContentTypeId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ContentLog>()
            .HasOne(x => x.ProductionMethod)
            .WithMany(x => x.ContentLogs)
            .HasForeignKey(x => x.ProductionMethodId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ContentLog>()
            .HasOne(x => x.Pic)
            .WithMany(x => x.ContentLogs)
            .HasForeignKey(x => x.PicId)
            .OnDelete(DeleteBehavior.SetNull);

        // ContentMetric
        modelBuilder.Entity<ContentMetric>()
            .HasOne(x => x.ContentLog)
            .WithMany(x => x.Metrics)
            .HasForeignKey(x => x.ContentLogId)
            .OnDelete(DeleteBehavior.Cascade);

        // Decimal precision
        modelBuilder.Entity<ContentMetric>()
            .Property(x => x.AverageWatch)
            .HasPrecision(18, 2);

        modelBuilder.Entity<ContentMetric>()
            .Property(x => x.FullWatchRate)
            .HasPrecision(18, 2);

        // Seed ContentType
        modelBuilder.Entity<ContentType>().HasData(
            new ContentType
            {
                Id = 1,
                Code = "KK",
                Name = "Keranjang Kuning",
                Color = "yellow",
                IsActive = true,
                CreatedAt = new DateTime(2026, 1, 1),
                UpdatedAt = new DateTime(2026, 1, 1)
            },
            new ContentType
            {
                Id = 2,
                Code = "NON_KK",
                Name = "Non-KK",
                Color = "blue",
                IsActive = true,
                CreatedAt = new DateTime(2026, 1, 1),
                UpdatedAt = new DateTime(2026, 1, 1)
            },
            new ContentType
            {
                Id = 3,
                Code = "AUTO_GMV_LIVE",
                Name = "Auto GMV Live",
                Color = "purple",
                IsActive = true,
                CreatedAt = new DateTime(2026, 1, 1),
                UpdatedAt = new DateTime(2026, 1, 1)
            }
        );

        // Seed ProductionMethod
        modelBuilder.Entity<ProductionMethod>().HasData(
            new ProductionMethod
            {
                Id = 1,
                Code = "SELF_PRODUCE",
                Name = "Self Produce",
                IsActive = true
            },
            new ProductionMethod
            {
                Id = 2,
                Code = "AI_PRODUCE",
                Name = "AI Produce",
                IsActive = true
            }
        );
    }
}