using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services.WinningContent;
using Xunit;

namespace KaleContentOps.Tests.WinningContent;

/// <summary>
/// Phase 2D tests: Content Composition counts and exact percentages.
/// Locked rules under test: composition uses CONTENT COUNTS (never Views/ER/baseline/
/// multiplier/median); exactly NON_KK + KK + AUTO_GMV_LIVE; Auto GMV Live always
/// included including the denominator (even when archived); TotalCount = 0 -> NULL
/// percentages + HasData=false (no fabricated split); zero-count categories stay
/// present with 0%; unclassified content excluded; same period population as the
/// other Winning Content sections.
/// </summary>
public class WinningContentCompositionTests
{
    private const string NonKk = "NON_KK";
    private const string Kk = "KK";
    private const string AutoGmv = "AUTO_GMV_LIVE";

    private static WinningContentFilter Filter() => new()
    {
        StartDate = new DateTime(2026, 9, 10),
        EndDate = new DateTime(2026, 9, 16)
    };

    private sealed class Host : IDisposable
    {
        public AppDbContext Db { get; }
        public WinningContentService Service { get; }

        public Host()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Db = new AppDbContext(options);

            Db.ContentTypes.AddRange(
                new ContentType { Code = NonKk, Name = "Non-KK" },
                new ContentType { Code = Kk, Name = "Keranjang Kuning" },
                new ContentType { Code = AutoGmv, Name = "Auto GMV Live" });
            Db.SaveChanges();

            Service = new WinningContentService(Db);
        }

        public ContentLog Log(DateTime postTime, string? typeCode, bool archived = false, long? views = 100)
        {
            var log = new ContentLog
            {
                VideoId = Guid.NewGuid().ToString("N"),
                Title = $"T-{Db.ContentLogs.Count() + 1}",
                Username = "creator",
                VideoPostTime = postTime,
                ContentTypeId = typeCode == null ? null : Db.ContentTypes.Single(c => c.Code == typeCode).Id,
                IsArchived = archived
            };
            Db.ContentLogs.Add(log);
            Db.ContentMetrics.Add(new ContentMetric
            {
                ContentLogId = log.Id,
                Views = views,
                Likes = 1,
                Comments = 0,
                Shares = 0,
                CapturedAt = DateTime.UtcNow
            });
            Db.SaveChanges();
            return log;
        }

        public void Dispose() => Db.Dispose();
    }

    private static DateTime PeriodDay(int day) => new(2026, 9, 10 + day - 1, 12, 0, 0);

    // ------------------------------------------------------------------ calculator unit tests

    [Fact]
    public void ComputeComposition_MockupCounts_ExactPercentages()
    {
        var c = WinningContentCalculator.ComputeComposition(nonKkCount: 10, kkCount: 4, autoGmvLiveCount: 1);

        Assert.Equal(15, c.TotalCount);
        Assert.True(c.HasData);
        Assert.Equal(10m / 15m * 100m, c.NonKkPercentage);   // 66.666...
        Assert.Equal(4m / 15m * 100m, c.KkPercentage);       // 26.666...
        Assert.Equal(1m / 15m * 100m, c.AutoGmvLivePercentage); // 6.666...
        // Full precision preserved: never rounded to a display integer or 2-dp value.
        Assert.NotEqual(67m, c.NonKkPercentage);
        Assert.NotEqual(66.67m, c.NonKkPercentage);
    }

    [Fact]
    public void ComputeComposition_AllZero_HasDataFalse_PercentagesNull()
    {
        var c = WinningContentCalculator.ComputeComposition(0, 0, 0);

        Assert.Equal(0, c.TotalCount);
        Assert.False(c.HasData);
        Assert.Null(c.NonKkPercentage);       // explicit no-data state
        Assert.Null(c.KkPercentage);          // never 0%-for-everyone, never 33.33%
        Assert.Null(c.AutoGmvLivePercentage);
        Assert.All(c.Categories, cat => Assert.Null(cat.Percentage));
    }

    [Fact]
    public void ComputeComposition_ZeroCategory_StaysZero_AndPresent()
    {
        var c = WinningContentCalculator.ComputeComposition(nonKkCount: 10, kkCount: 0, autoGmvLiveCount: 1);

        Assert.Equal(11, c.TotalCount);
        Assert.Equal(0m, c.KkPercentage);                        // exactly 0, not NULL
        Assert.Equal(10m / 11m * 100m, c.NonKkPercentage);
        Assert.Equal(1m / 11m * 100m, c.AutoGmvLivePercentage);
        Assert.Equal(3, c.Categories.Count);                     // all slots always present
        Assert.Contains(c.Categories, cat => cat.ContentTypeCode == Kk && cat.Count == 0 && cat.Percentage == 0m);
    }

    [Fact]
    public void ComputeComposition_SingleCategory_Is100Percent()
    {
        var c = WinningContentCalculator.ComputeComposition(nonKkCount: 10, kkCount: 0, autoGmvLiveCount: 0);

        Assert.Equal(10, c.TotalCount);
        Assert.Equal(100m, c.NonKkPercentage);
        Assert.Equal(0m, c.KkPercentage);
        Assert.Equal(0m, c.AutoGmvLivePercentage);
    }

    [Fact]
    public void ComputeComposition_Total_Is_Sum_Of_Three_Categories()
    {
        for (var nonKk = 0; nonKk <= 2; nonKk++)
        for (var kk = 0; kk <= 2; kk++)
        for (var auto = 0; auto <= 2; auto++)
        {
            var c = WinningContentCalculator.ComputeComposition(nonKk, kk, auto);
            Assert.Equal(nonKk + kk + auto, c.TotalCount);
        }
    }

    // ------------------------------------------------------------------ end-to-end through BuildAsync

    [Fact]
    public async Task Composition_MockupSample_10_4_1_EndToEnd()
    {
        using var host = new Host();
        // NON_KK = 10
        for (var i = 0; i < 10; i++) host.Log(PeriodDay(1 + (i % 7)), NonKk);
        // KK = 4
        for (var i = 0; i < 4; i++) host.Log(PeriodDay(1 + i), Kk);
        // AUTO_GMV_LIVE = 1
        host.Log(PeriodDay(3), AutoGmv);

        var data = await host.Service.BuildAsync(Filter());
        var c = data.CompositionSummary;

        Assert.Equal(15, c.TotalCount);
        Assert.Equal(10, c.NonKkCount);
        Assert.Equal(4, c.KkCount);
        Assert.Equal(1, c.AutoGmvLiveCount);
        Assert.True(c.HasData);

        // Precise values validated, not display text (UI may later show 67 / 27 / 7).
        Assert.Equal(10m / 15m * 100m, c.NonKkPercentage);
        Assert.Equal(4m / 15m * 100m, c.KkPercentage);
        Assert.Equal(1m / 15m * 100m, c.AutoGmvLivePercentage);
    }

    [Fact]
    public async Task Composition_Archived_AutoGmv_Counts_In_Denominator()
    {
        using var host = new Host();
        for (var i = 0; i < 10; i++) host.Log(PeriodDay(1 + (i % 7)), NonKk);
        for (var i = 0; i < 4; i++) host.Log(PeriodDay(1 + i), Kk);
        host.Log(PeriodDay(3), AutoGmv, archived: true); // archived: MUST still count

        var data = await host.Service.BuildAsync(Filter()); // IncludeArchived=false default
        var c = data.CompositionSummary;

        Assert.Equal(15, c.TotalCount);          // denominator includes the archived Auto GMV row
        Assert.Equal(1, c.AutoGmvLiveCount);
        Assert.Equal(1m / 15m * 100m, c.AutoGmvLivePercentage);
    }

    [Fact]
    public async Task Composition_Percentage_Equals_CategoryCount_Over_Total()
    {
        using var host = new Host();
        for (var i = 0; i < 10; i++) host.Log(PeriodDay(1 + (i % 7)), NonKk);
        for (var i = 0; i < 4; i++) host.Log(PeriodDay(1 + i), Kk);
        host.Log(PeriodDay(3), AutoGmv);

        var data = await host.Service.BuildAsync(Filter());
        var c = data.CompositionSummary;

        Assert.Equal(c.NonKkCount / (decimal)c.TotalCount * 100m, c.NonKkPercentage);
        Assert.Equal(c.KkCount / (decimal)c.TotalCount * 100m, c.KkPercentage);
        Assert.Equal(c.AutoGmvLiveCount / (decimal)c.TotalCount * 100m, c.AutoGmvLivePercentage);

        // Per-category slots mirror the aggregate fields, in locked order NON_KK, KK, AUTO_GMV_LIVE.
        Assert.Equal(new[] { NonKk, Kk, AutoGmv }, c.Categories.Select(x => x.ContentTypeCode).ToArray());
        Assert.Equal(c.NonKkPercentage, c.Categories[0].Percentage);
        Assert.Equal(c.KkPercentage, c.Categories[1].Percentage);
        Assert.Equal(c.AutoGmvLivePercentage, c.Categories[2].Percentage);
    }

    [Fact]
    public async Task Composition_Zero_Category_EndToEnd_NoException()
    {
        using var host = new Host();
        for (var i = 0; i < 10; i++) host.Log(PeriodDay(1 + (i % 7)), NonKk);
        host.Log(PeriodDay(3), AutoGmv);
        // KK: zero content.

        var data = await host.Service.BuildAsync(Filter());
        var c = data.CompositionSummary;

        Assert.Equal(11, c.TotalCount);
        Assert.Equal(0, c.KkCount);
        Assert.Equal(0m, c.KkPercentage);
        Assert.Equal(10m / 11m * 100m, c.NonKkPercentage);
        Assert.Equal(1m / 11m * 100m, c.AutoGmvLivePercentage);
    }

    [Fact]
    public async Task Composition_AllZero_Period_EndToEnd()
    {
        using var host = new Host();
        // Content exists but OUTSIDE the selected period -> composition has no data.
        host.Log(new DateTime(2026, 9, 1, 12, 0, 0), NonKk);
        host.Log(new DateTime(2026, 9, 2, 12, 0, 0), Kk);
        host.Log(new DateTime(2026, 9, 3, 12, 0, 0), AutoGmv);

        var data = await host.Service.BuildAsync(Filter());
        var c = data.CompositionSummary;

        Assert.Equal(0, c.TotalCount);
        Assert.False(c.HasData);
        Assert.Null(c.NonKkPercentage);
        Assert.Null(c.KkPercentage);
        Assert.Null(c.AutoGmvLivePercentage);
        Assert.Empty(data.Items);
    }

    [Fact]
    public async Task Composition_Single_Category_EndToEnd()
    {
        using var host = new Host();
        for (var i = 0; i < 10; i++) host.Log(PeriodDay(1 + (i % 7)), NonKk);
        // KK and AUTO_GMV_LIVE: zero.

        var data = await host.Service.BuildAsync(Filter());
        var c = data.CompositionSummary;

        Assert.Equal(10, c.TotalCount);
        Assert.Equal(100m, c.NonKkPercentage);
        Assert.Equal(0m, c.KkPercentage);
        Assert.Equal(0m, c.AutoGmvLivePercentage);
    }

    [Fact]
    public async Task Composition_Is_Count_Based_Never_Views_Weighted()
    {
        using var host = new Host();
        // NON_KK: 1 video with a HUGE view count.
        host.Log(PeriodDay(1), NonKk, views: 1_000_000);
        // KK: 9 videos with tiny view counts.
        for (var i = 0; i < 9; i++) host.Log(PeriodDay(2), Kk, views: 100);
        // AUTO_GMV_LIVE: zero.

        var data = await host.Service.BuildAsync(Filter());
        var c = data.CompositionSummary;

        Assert.Equal(10, c.TotalCount);
        Assert.Equal(1, c.NonKkCount);
        Assert.Equal(9, c.KkCount);
        Assert.Equal(0, c.AutoGmvLiveCount);

        Assert.Equal(10m, c.NonKkPercentage);   // count-based: 1/10
        Assert.Equal(90m, c.KkPercentage);      // count-based: 9/10
        Assert.Equal(0m, c.AutoGmvLivePercentage);
        // A views-weighted split would give ~99.987% NON_KK - explicitly wrong.
        Assert.NotEqual(99.987m, c.NonKkPercentage);
    }

    [Fact]
    public async Task Composition_Unclassified_Content_Is_Excluded()
    {
        using var host = new Host();
        for (var i = 0; i < 3; i++) host.Log(PeriodDay(1), NonKk);
        host.Log(PeriodDay(2), null);  // ContentTypeId NULL -> excluded, never forced into NON_KK
        host.Log(PeriodDay(3), null);

        var data = await host.Service.BuildAsync(Filter());
        var c = data.CompositionSummary;

        Assert.Equal(3, c.TotalCount);          // only classified content counts
        Assert.Equal(3, c.NonKkCount);
        Assert.Equal(100m, c.NonKkPercentage);
        Assert.Equal(0m, c.KkPercentage);
        Assert.Equal(0m, c.AutoGmvLivePercentage);
        Assert.Equal(3, data.Items.Count);      // consistent with the item population
    }

    [Fact]
    public async Task Composition_Uses_Same_Period_Population_As_Other_Sections()
    {
        using var host = new Host();
        // 2 NON_KK inside; 1 NON_KK just before period start; 1 KK just after period end.
        host.Log(PeriodDay(1), NonKk);
        host.Log(PeriodDay(7), NonKk);
        host.Log(new DateTime(2026, 9, 9, 23, 59, 59), NonKk);  // before start
        host.Log(new DateTime(2026, 9, 17, 0, 0, 0), Kk);       // after end

        var data = await host.Service.BuildAsync(Filter());
        var c = data.CompositionSummary;

        Assert.Equal(2, c.TotalCount);          // out-of-range rows must not affect composition
        Assert.Equal(2, c.NonKkCount);
        Assert.Equal(0, c.KkCount);
        Assert.Equal(0, c.AutoGmvLiveCount);
        Assert.Equal(2, data.Items.Count);      // same population as Items/BelowMedian/etc.
        Assert.Equal(c.TotalCount, data.Items.Count);
    }
}
