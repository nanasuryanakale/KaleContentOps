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
/// Phase 2C tests: exact median Views per content type and Below Median selection.
/// Locked rules under test: median uses Views only; per content type; exact middle
/// value (even count = decimal average of the two middle values, no integer
/// truncation); NULL Views excluded from the population; no valid Views -> NULL
/// median (never 0); Below Median is STRICT Views &lt; median; Auto GMV Live always
/// participates (archive status never removes it).
/// </summary>
public class WinningContentMedianTests
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

        public ContentLog LogWithViews(DateTime postTime, string typeCode, long? views, bool archived = false)
        {
            var log = new ContentLog
            {
                VideoId = Guid.NewGuid().ToString("N"),
                Title = $"T-{Db.ContentLogs.Count() + 1}",
                Username = "creator",
                VideoUrl = "https://tiktok.com/@creator/video/1",
                VideoPostTime = postTime,
                ContentTypeId = Db.ContentTypes.Single(c => c.Code == typeCode).Id,
                IsArchived = archived
            };
            Db.ContentLogs.Add(log);
            Db.ContentMetrics.Add(new ContentMetric
            {
                ContentLogId = log.Id,
                Views = views,
                Likes = views.HasValue ? 1 : null, // valid ER not needed for median tests
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
    public void ComputeMedianViews_OddCount_ReturnsMiddleValue()
    {
        Assert.Equal(200m, WinningContentCalculator.ComputeMedianViews(new long?[] { 500, 100, 200 }));
    }

    [Fact]
    public void ComputeMedianViews_EvenCount_AveragesMiddleValues()
    {
        // [100, 200, 500, 800] -> (200 + 500) / 2 = 350
        Assert.Equal(350m, WinningContentCalculator.ComputeMedianViews(new long?[] { 100, 200, 500, 800 }));
    }

    [Fact]
    public void ComputeMedianViews_EvenCount_DoesNotTruncate_Integers()
    {
        // Integer division would give 1; decimal averaging must give 1.5.
        Assert.Equal(1.5m, WinningContentCalculator.ComputeMedianViews(new long?[] { 1, 2 }));
    }

    [Fact]
    public void ComputeMedianViews_Nulls_Are_Excluded_From_Population()
    {
        // [NULL, 100, 200, 500] -> valid = [100, 200, 500] -> median 200 (NULL not counted).
        Assert.Equal(200m, WinningContentCalculator.ComputeMedianViews(new long?[] { null, 100, 200, 500 }));
    }

    [Fact]
    public void ComputeMedianViews_AllNull_OrEmpty_ReturnsNull_NeverZero()
    {
        Assert.Null(WinningContentCalculator.ComputeMedianViews(new long?[] { null, null }));
        Assert.Null(WinningContentCalculator.ComputeMedianViews(Array.Empty<long?>()));
        Assert.Null(WinningContentCalculator.ComputeMedianViews(null));
    }

    [Fact]
    public void ComputeMedianViews_SingleValue_And_ZeroAreRespected()
    {
        Assert.Equal(42m, WinningContentCalculator.ComputeMedianViews(new long?[] { 42 }));
        Assert.Equal(0m, WinningContentCalculator.ComputeMedianViews(new long?[] { 0, 0, 10 })); // 0 is a valid value
    }

    [Fact]
    public void IsBelowMedian_Is_Strict_LessThan()
    {
        Assert.True(WinningContentCalculator.IsBelowMedian(499, 500m));
        Assert.False(WinningContentCalculator.IsBelowMedian(500, 500m)); // equal -> EXCLUDED (never <=)
        Assert.False(WinningContentCalculator.IsBelowMedian(501, 500m));
    }

    [Fact]
    public void IsBelowMedian_NullViews_Or_NullMedian_Is_Never_Below()
    {
        Assert.False(WinningContentCalculator.IsBelowMedian(null, 500m));
        Assert.False(WinningContentCalculator.IsBelowMedian(100, null));
        Assert.False(WinningContentCalculator.IsBelowMedian(null, null));
    }

    // ------------------------------------------------------------------ end-to-end through BuildAsync

    [Fact]
    public async Task Median_OddCount_EndToEnd()
    {
        using var host = new Host();
        host.LogWithViews(PeriodDay(1), NonKk, 100);
        host.LogWithViews(PeriodDay(2), NonKk, 200);
        host.LogWithViews(PeriodDay(3), NonKk, 500);

        var data = await host.Service.BuildAsync(Filter());
        var median = data.Medians.Single(m => m.ContentTypeCode == NonKk);

        Assert.Equal(200m, median.MedianViews);
        Assert.Equal(3, median.ValidViewsCount);
        Assert.Equal(0, median.NullViewsCount);
    }

    [Fact]
    public async Task Median_EvenCount_EndToEnd_NoIntegerTruncation()
    {
        using var host = new Host();
        host.LogWithViews(PeriodDay(1), Kk, 100);
        host.LogWithViews(PeriodDay(2), Kk, 200);
        host.LogWithViews(PeriodDay(3), Kk, 500);
        host.LogWithViews(PeriodDay(4), Kk, 800);

        var data = await host.Service.BuildAsync(Filter());
        var median = data.Medians.Single(m => m.ContentTypeCode == Kk);

        Assert.Equal(350m, median.MedianViews); // (200+500)/2 - decimal, not 350 after truncation issues
        Assert.Equal(4, median.ValidViewsCount);
    }

    [Fact]
    public async Task Median_NullViews_Excluded_EndToEnd()
    {
        using var host = new Host();
        host.LogWithViews(PeriodDay(1), NonKk, null); // no metric Views -> excluded, counted as NULL
        host.LogWithViews(PeriodDay(2), NonKk, 100);
        host.LogWithViews(PeriodDay(3), NonKk, 200);
        host.LogWithViews(PeriodDay(4), NonKk, 500);

        var data = await host.Service.BuildAsync(Filter());
        var median = data.Medians.Single(m => m.ContentTypeCode == NonKk);

        Assert.Equal(200m, median.MedianViews);
        Assert.Equal(3, median.ValidViewsCount);
        Assert.Equal(1, median.NullViewsCount);
    }

    [Fact]
    public async Task Median_NoValidViews_ReturnsNull_NotZero()
    {
        using var host = new Host();
        host.LogWithViews(PeriodDay(1), Kk, null);
        host.LogWithViews(PeriodDay(2), Kk, null);

        var data = await host.Service.BuildAsync(Filter());
        var median = data.Medians.Single(m => m.ContentTypeCode == Kk);

        Assert.Null(median.MedianViews); // NULL, never 0
        Assert.Equal(0, median.ValidViewsCount);
        Assert.Equal(2, median.NullViewsCount);

        // And the empty Below Median section carries the NULL median without exception.
        var group = data.BelowMedian.Single(g => g.ContentTypeCode == Kk);
        Assert.Null(group.MedianViews);
        Assert.Empty(group.Entries);
    }

    [Fact]
    public async Task Median_Category_Isolation_Each_Type_Gets_Its_Own_Median()
    {
        using var host = new Host();
        foreach (var v in new[] { 100L, 200L, 300L }) host.LogWithViews(PeriodDay(1), NonKk, v);
        foreach (var v in new[] { 10L, 20L, 30L }) host.LogWithViews(PeriodDay(2), Kk, v);
        foreach (var v in new[] { 1000L, 2000L, 3000L }) host.LogWithViews(PeriodDay(3), AutoGmv, v);

        var data = await host.Service.BuildAsync(Filter());

        Assert.Equal(200m, data.Medians.Single(m => m.ContentTypeCode == NonKk).MedianViews);
        Assert.Equal(20m, data.Medians.Single(m => m.ContentTypeCode == Kk).MedianViews);
        Assert.Equal(2000m, data.Medians.Single(m => m.ContentTypeCode == AutoGmv).MedianViews);

        // A category with NO content has its own NULL median - never borrowed (locked rule 8).
        Assert.Equal(3, data.Medians.Count);
    }

    [Fact]
    public async Task Median_Empty_Category_Is_Null_And_Does_Not_Borrow_Others()
    {
        using var host = new Host();
        foreach (var v in new[] { 100L, 200L, 300L }) host.LogWithViews(PeriodDay(1), NonKk, v);
        // KK and AUTO_GMV_LIVE: no content at all.

        var data = await host.Service.BuildAsync(Filter());

        var kk = data.Medians.Single(m => m.ContentTypeCode == Kk);
        var autoGmv = data.Medians.Single(m => m.ContentTypeCode == AutoGmv);
        Assert.Null(kk.MedianViews);
        Assert.Null(autoGmv.MedianViews);
        Assert.NotEqual(200m, kk.MedianViews); // explicitly NOT the NON_KK median
    }

    // ------------------------------------------------------------------ below median

    [Fact]
    public async Task BelowMedian_Strict_Comparison_EndToEnd()
    {
        using var host = new Host();
        // Median of [500, 500, 800] = 500. Views 499 / 500 / 501 around it.
        host.LogWithViews(PeriodDay(1), NonKk, 500);
        host.LogWithViews(PeriodDay(2), NonKk, 500);
        host.LogWithViews(PeriodDay(3), NonKk, 800);
        var v499 = host.LogWithViews(PeriodDay(4), NonKk, 499);
        var v500 = host.LogWithViews(PeriodDay(5), NonKk, 500);
        var v501 = host.LogWithViews(PeriodDay(6), NonKk, 501);

        var data = await host.Service.BuildAsync(Filter());
        var group = data.BelowMedian.Single(g => g.ContentTypeCode == NonKk);

        Assert.Equal(500m, group.MedianViews);
        var entry = Assert.Single(group.Entries);
        Assert.Equal(v499.Id, entry.ContentLogId); // 499 < 500 -> INCLUDE
        Assert.DoesNotContain(group.Entries, e => e.ContentLogId == v500.Id); // == median -> EXCLUDE
        Assert.DoesNotContain(group.Entries, e => e.ContentLogId == v501.Id); // > median -> EXCLUDE
    }

    [Fact]
    public async Task BelowMedian_NullViews_Never_Included()
    {
        using var host = new Host();
        host.LogWithViews(PeriodDay(1), NonKk, 100);
        host.LogWithViews(PeriodDay(2), NonKk, 300);
        var nullViews = host.LogWithViews(PeriodDay(3), NonKk, null);

        var data = await host.Service.BuildAsync(Filter());
        var group = data.BelowMedian.Single(g => g.ContentTypeCode == NonKk);

        Assert.Equal(200m, group.MedianViews);
        // 100 < 200 is below median; the NULL-Views row can never satisfy the condition.
        Assert.DoesNotContain(group.Entries, e => e.ContentLogId == nullViews.Id);
        Assert.All(group.Entries, e => Assert.True(e.Views.HasValue));
    }

    [Fact]
    public async Task BelowMedian_Grouped_Per_Category_Never_Combined()
    {
        using var host = new Host();
        // NON_KK: [100, 200, 300] -> median 200 -> below = {100}
        foreach (var v in new[] { 100L, 200L, 300L }) host.LogWithViews(PeriodDay(1), NonKk, v);
        // KK: [10, 20, 30] -> median 20 -> below = {10}; 10 would NOT be below NON_KK's median
        foreach (var v in new[] { 10L, 20L, 30L }) host.LogWithViews(PeriodDay(2), Kk, v);
        // AUTO_GMV_LIVE: [1000, 2000, 3000] -> median 2000 -> below = {1000}
        foreach (var v in new[] { 1000L, 2000L, 3000L }) host.LogWithViews(PeriodDay(3), AutoGmv, v);

        var data = await host.Service.BuildAsync(Filter());

        var nonKk = data.BelowMedian.Single(g => g.ContentTypeCode == NonKk);
        var kk = data.BelowMedian.Single(g => g.ContentTypeCode == Kk);
        var autoGmv = data.BelowMedian.Single(g => g.ContentTypeCode == AutoGmv);

        Assert.Equal(200m, nonKk.MedianViews);
        Assert.Equal(new long?[] { 100 }, nonKk.Entries.Select(e => e.Views).ToArray());

        Assert.Equal(20m, kk.MedianViews);
        Assert.Equal(new long?[] { 10 }, kk.Entries.Select(e => e.Views).ToArray());

        Assert.Equal(2000m, autoGmv.MedianViews);
        Assert.Equal(new long?[] { 1000 }, autoGmv.Entries.Select(e => e.Views).ToArray());

        // Exactly one section per category, no merged pool.
        Assert.Equal(3, data.BelowMedian.Count);
        Assert.Equal(1, nonKk.Entries.Count);
        Assert.Equal(1, kk.Entries.Count);
        Assert.Equal(1, autoGmv.Entries.Count);
    }

    [Fact]
    public async Task Archived_AutoGmv_Participates_In_Median_And_BelowMedian()
    {
        using var host = new Host();
        // Archived items carry valid Views and must count in the median population.
        host.LogWithViews(PeriodDay(1), AutoGmv, 1000, archived: true);
        host.LogWithViews(PeriodDay(2), AutoGmv, 2000, archived: true);
        host.LogWithViews(PeriodDay(3), AutoGmv, 3000, archived: false);
        // Archived below-median item must still be selected.
        var archivedBelow = host.LogWithViews(PeriodDay(4), AutoGmv, 500, archived: true);

        var data = await host.Service.BuildAsync(Filter()); // IncludeArchived=false default
        var median = data.Medians.Single(m => m.ContentTypeCode == AutoGmv);
        var group = data.BelowMedian.Single(g => g.ContentTypeCode == AutoGmv);

        // Median of [500, 1000, 2000, 3000] = (1000+2000)/2 = 1500 - archived rows counted.
        Assert.Equal(1500m, median.MedianViews);
        Assert.Equal(4, median.ValidViewsCount);

        Assert.Contains(group.Entries, e => e.ContentLogId == archivedBelow.Id);
        Assert.All(group.Entries, e => Assert.True(e.Views < group.MedianViews));
    }

    [Fact]
    public async Task BelowMedian_Entry_Carries_Mockup_Fields()
    {
        using var host = new Host();
        // Population [250, 1000, 2000] -> median 1000; the 250-Views item is strictly below it.
        host.LogWithViews(PeriodDay(1), NonKk, 1000);
        host.LogWithViews(PeriodDay(3), NonKk, 2000);
        var below = host.LogWithViews(PeriodDay(2), NonKk, 250);

        var data = await host.Service.BuildAsync(Filter());
        var group = data.BelowMedian.Single(g => g.ContentTypeCode == NonKk);
        Assert.Equal(1000m, group.MedianViews);
        var entry = group.Entries.Single(e => e.ContentLogId == below.Id);

        Assert.False(string.IsNullOrEmpty(entry.VideoId));
        Assert.False(string.IsNullOrEmpty(entry.Title));
        Assert.False(string.IsNullOrEmpty(entry.Username));
        Assert.False(string.IsNullOrEmpty(entry.VideoUrl));
        Assert.Equal(NonKk, entry.ContentTypeCode);
        Assert.Equal(250, entry.Views);
        Assert.Equal(1000m, entry.MedianViews);
        Assert.NotNull(entry.LatestMetricCapturedAt);
        Assert.Equal(PeriodDay(2).Date, entry.VideoPostTime!.Value.Date);
    }

    // ------------------------------------------------------------------ mockup-like sample

    [Fact]
    public async Task Mockup_Sample_NonKk_Median_8371()
    {
        using var host = new Host();
        foreach (var v in new[] { 2673L, 5000L, 8371L, 10000L, 15000L })
        {
            host.LogWithViews(PeriodDay(1), NonKk, v);
        }
        // KK with its own dataset: [1200, 3400, 9900] -> median 3400
        foreach (var v in new[] { 1200L, 3400L, 9900L }) host.LogWithViews(PeriodDay(2), Kk, v);
        // AUTO_GMV_LIVE: [20000, 45000] -> median 32500 (even count, decimal average)
        foreach (var v in new[] { 20000L, 45000L }) host.LogWithViews(PeriodDay(3), AutoGmv, v);

        var data = await host.Service.BuildAsync(Filter());

        var nonKk = data.Medians.Single(m => m.ContentTypeCode == NonKk);
        Assert.Equal(8371m, nonKk.MedianViews); // middle of 5 sorted values

        var nonKkBelow = data.BelowMedian.Single(g => g.ContentTypeCode == NonKk);
        Assert.Equal(new long?[] { 2673, 5000 }, nonKkBelow.Entries.Select(e => e.Views).OrderBy(v => v).ToArray());
        Assert.DoesNotContain(nonKkBelow.Entries, e => e.Views == 8371L); // equal to median excluded

        Assert.Equal(3400m, data.Medians.Single(m => m.ContentTypeCode == Kk).MedianViews);
        Assert.Equal(32500m, data.Medians.Single(m => m.ContentTypeCode == AutoGmv).MedianViews);
    }
}
