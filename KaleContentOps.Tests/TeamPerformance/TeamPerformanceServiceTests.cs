using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using KaleContentOps.Services.Targets;
using KaleContentOps.Services.TeamPerformance;
using KaleContentOps.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KaleContentOps.Tests.TeamPerformance;

/// <summary>
/// Team Performance service tests. Covers the locked business rules that can be validated
/// today: period filtering on VideoPostTime (half-open, Jakarta calendar boundaries),
/// archived-inclusive aggregation, PicId NULL handling, historical attribution for a PIC that
/// is now inactive, latest-metric selection, null-safe metrics/views, empty period and PIC
/// ranking.
///
/// NOT covered here: employment-period eligibility boundaries (JoinDate / ResignDate overlap)
/// - those live in TeamPerformanceEmploymentPeriodTests (Phase 3A), which exercises the single
/// ApplyEmploymentPeriodFilter seam. EmploymentPeriodDataAvailable is now reported as true.
/// </summary>
public class TeamPerformanceServiceTests
{
    private sealed class TestHost
    {
        public AppDbContext Db { get; }
        public TeamPerformanceService Service { get; }

        public TestHost()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Db = new AppDbContext(options);
            // Phase 4: TeamPerformanceService now also takes ITargetService. The REAL
            // TargetService runs over the same store so target resolution stays genuine
            // (no stub, no duplicated SCD-2 logic). Assertions below are unchanged.
            var clock = new StubShopTimeZone(new DateOnly(2026, 9, 24));
            Service = new TeamPerformanceService(Db, clock, new TargetService(Db, clock));
        }

        public MasterPic Pic(string name, bool isActive = true)
        {
            var pic = new MasterPic { Name = name, IsActive = isActive };
            Db.MasterPics.Add(pic);
            Db.SaveChanges();
            return pic;
        }

        public ContentLog Log(DateTime? postTime, int? picId, bool isArchived = false)
        {
            var log = new ContentLog
            {
                VideoId = $"vid-{Guid.NewGuid():N}",
                VideoPostTime = postTime,
                PicId = picId,
                IsArchived = isArchived,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.ContentLogs.Add(log);
            Db.SaveChanges();
            return log;
        }

        public ContentMetric Metric(long contentLogId, long? views, DateTime capturedAt)
        {
            var metric = new ContentMetric { ContentLogId = contentLogId, Views = views, CapturedAt = capturedAt };
            Db.ContentMetrics.Add(metric);
            Db.SaveChanges();
            return metric;
        }

        public Task<TeamPerformanceViewModel> BuildAsync(DateTime start, DateTime end) =>
            Service.BuildAsync(new TeamPerformanceFilter { StartDate = start, EndDate = end }, CancellationToken.None);
    }

    private sealed class StubShopTimeZone : IShopTimeZone
    {
        public StubShopTimeZone(DateOnly today) => TodayValue = today;
        public DateOnly TodayValue { get; set; }
        public string TimeZoneId => "Asia/Jakarta";
        public DateTimeOffset ToShopLocal(DateTimeOffset utcInstant) => utcInstant;
        public DateOnly GetShopLocalDate(DateTimeOffset utcInstant) => DateOnly.FromDateTime(utcInstant.DateTime);
        public DateOnly Today() => TodayValue;
        public DateTime ToDateTime(DateOnly shopLocalDate) => shopLocalDate.ToDateTime(TimeOnly.MinValue);
        public DateTime TodayMidnight() => ToDateTime(TodayValue);
    }

    // ============================================================
    // 1) Date period filtering (half-open, shop-local boundaries)
    // ============================================================

    [Fact]
    public async Task Period_Includes_Start_Mid_And_EndDay_Excludes_NextDayMidnight_And_Before()
    {
        var host = new TestHost();
        host.Log(new DateTime(2026, 8, 31, 23, 59, 59), null); // day before -> excluded
        host.Log(new DateTime(2026, 9, 1, 0, 0, 0), null);     // exact start -> included
        host.Log(new DateTime(2026, 9, 3, 12, 30, 0), null);   // mid -> included
        host.Log(new DateTime(2026, 9, 7, 23, 59, 59), null);  // exact end of day -> included
        host.Log(new DateTime(2026, 9, 8, 0, 0, 0), null);     // next day midnight -> excluded

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(3, model.TotalContentCount);
        Assert.Equal(new DateTime(2026, 9, 1), model.StartDate);
        Assert.Equal(new DateTime(2026, 9, 7), model.EndDate);
    }

    [Fact]
    public async Task Period_InvertedRange_IsNormalized()
    {
        var host = new TestHost();
        host.Log(new DateTime(2026, 9, 3, 9, 0, 0), null);

        var model = await host.BuildAsync(new DateTime(2026, 9, 7), new DateTime(2026, 9, 1));

        Assert.Equal(new DateTime(2026, 9, 1), model.StartDate);
        Assert.Equal(new DateTime(2026, 9, 7), model.EndDate);
        Assert.Equal(1, model.TotalContentCount);
    }

    [Fact]
    public async Task Period_SingleDay_Includes_WholeJakartaDay()
    {
        var host = new TestHost();
        // 2026-09-03 is WIB (UTC+7); VideoPostTime is stored as shop-local wall time and the
        // period boundary is the shop-local calendar midnight.
        host.Log(new DateTime(2026, 9, 3, 6, 30, 0), null);  // 06:30 WIB
        host.Log(new DateTime(2026, 9, 3, 23, 0, 0), null);  // 23:00 WIB
        host.Log(new DateTime(2026, 9, 4, 0, 0, 0), null);   // next shop-local day

        var model = await host.BuildAsync(new DateTime(2026, 9, 3), new DateTime(2026, 9, 3));

        Assert.Equal(2, model.TotalContentCount);
        Assert.Equal("Asia/Jakarta", model.TimeZoneId);
    }

    [Fact]
    public void ShopTimeZone_Resolves_JakartaMidnights()
    {
        var tz = new ShopTimeZone(Microsoft.Extensions.Options.Options.Create(new ShopTimeZoneOptions()));
        Assert.Equal("Asia/Jakarta", tz.TimeZoneId);
        Assert.Equal(new DateTime(2026, 9, 3, 0, 0, 0), tz.ToDateTime(new DateOnly(2026, 9, 3)));
        Assert.Equal(new DateTime(2026, 9, 4, 0, 0, 0), tz.ToDateTime(new DateOnly(2026, 9, 3).AddDays(1)));
    }

    // ============================================================
    // 3) Archived content is always included (locked rule 1)
    // ============================================================

    [Fact]
    public async Task ArchivedContent_Is_Always_Included()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        var active = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), pic.Id, isArchived: false);
        var archived = host.Log(new DateTime(2026, 9, 2, 11, 0, 0), pic.Id, isArchived: true);
        host.Metric(active.Id, 100, new DateTime(2026, 9, 3));
        host.Metric(archived.Id, 40, new DateTime(2026, 9, 3));

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(2, model.TotalContentCount);
        Assert.Equal(140, model.TotalViews);
        Assert.Equal(2, model.Pics.Single(r => r.PicId == pic.Id).ContentCount);
    }

    // ============================================================
    // 4/7/10) PicId NULL ("Belum Diisi") is team workload only
    // ============================================================

    [Fact]
    public async Task NullPic_CountsInTeamTotals_ButIsNotAnIndividualPicRow()
    {
        var host = new TestHost();
        var pic = host.Pic("Budi");
        var assigned = host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id);
        var unassigned = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), null);
        host.Metric(assigned.Id, 200, new DateTime(2026, 9, 3));
        host.Metric(unassigned.Id, 80, new DateTime(2026, 9, 3));

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(2, model.TotalContentCount);
        Assert.Equal(280, model.TotalViews);
        Assert.Equal(1, model.AssignedContentCount);
        Assert.Equal(200, model.AssignedViews);
        Assert.Equal(1, model.UnassignedContentCount);
        Assert.Equal(80, model.UnassignedViews);
        // No row is named "Belum Diisi"; only the real PIC exists.
        Assert.Single(model.Pics);
        Assert.Equal("Budi", model.Pics[0].PicName);
    }

    // ============================================================
    // 5) Historical attribution for a now-inactive PIC
    // ============================================================

    [Fact]
    public async Task InactivePic_With_Historical_Content_Remains_Attributable()
    {
        var host = new TestHost();
        var resigned = host.Pic("Sari", isActive: false);
        var log = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), resigned.Id);
        host.Metric(log.Id, 350, new DateTime(2026, 9, 3));

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        var row = model.Pics.Single();
        Assert.Equal("Sari", row.PicName);
        Assert.False(row.IsActive);        // current status only, never eligibility
        Assert.Equal(1, row.ContentCount);
        Assert.Equal(350, row.TotalViews);
    }

    // ============================================================
    // 6) Fair denominator: eligible PICs with zero content still appear
    // ============================================================

    [Fact]
    public async Task ZeroContentPic_Is_Included_In_Denominator()
    {
        var host = new TestHost();
        var withContent = host.Pic("Ayu");
        host.Pic("Dian"); // no content
        host.Pic("Eka");  // no content

        var log = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), withContent.Id);
        host.Metric(log.Id, 10, new DateTime(2026, 9, 3));

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(3, model.EligiblePicCount);
        Assert.Equal(1, model.PicsWithContentCount);
        Assert.Equal(3, model.Pics.Count);                       // denominator rows kept
        Assert.Equal(2, model.Pics.Count(r => !r.HasContent));   // zero-content rows present
        Assert.All(model.Pics.Where(r => !r.HasContent), r => Assert.Null(r.Rank));
    }

    [Fact]
    public async Task EmploymentPeriodData_Is_Reported_Available_After_Phase3A()
    {
        var host = new TestHost();
        host.Pic("Ayu");

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        // Phase 3A: JoinDate/ResignDate are integrated, so the provisional-availability
        // warning must no longer be reported (the UI check for this lives in TeamPerformanceUiTests).
        Assert.True(model.EmploymentPeriodDataAvailable);
    }

    // ============================================================
    // 9) VideoPostTime NULL never enters a date period
    // ============================================================

    [Fact]
    public async Task NullVideoPostTime_Is_Excluded_From_Period()
    {
        var host = new TestHost();
        host.Log(null, null);
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), null);

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(1, model.TotalContentCount);
    }

    // ============================================================
    // 10) Content without a metric: counted, zero views
    // ============================================================

    [Fact]
    public async Task ContentWithoutMetric_Counts_WithZeroViews()
    {
        var host = new TestHost();
        var pic = host.Pic("Nina");
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), pic.Id); // no metric

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        var row = model.Pics.Single();
        Assert.Equal(1, row.ContentCount);
        Assert.Equal(0, row.TotalViews);
        Assert.Equal(0m, row.AverageViewsPerContent);
    }

    // ============================================================
    // 11/12) Latest metric selection across multiple metrics
    // ============================================================

    [Fact]
    public async Task LatestMetric_ByCapturedAt_Is_Used()
    {
        var host = new TestHost();
        var pic = host.Pic("Tio");
        var log = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), pic.Id);
        host.Metric(log.Id, 10, new DateTime(2026, 9, 2, 12, 0, 0));
        host.Metric(log.Id, 99, new DateTime(2026, 9, 4, 12, 0, 0)); // latest
        host.Metric(log.Id, 50, new DateTime(2026, 9, 3, 12, 0, 0));

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(99, model.TotalViews);
    }

    [Fact]
    public async Task LatestMetric_SameCapturedAt_Uses_HigherId()
    {
        var host = new TestHost();
        var pic = host.Pic("Wahyu");
        var log = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), pic.Id);
        var captured = new DateTime(2026, 9, 4, 12, 0, 0);
        host.Metric(log.Id, 20, captured);
        host.Metric(log.Id, 70, captured); // higher Id, same CapturedAt -> wins

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(70, model.TotalViews);
    }

    [Fact]
    public async Task NullViews_Contribute_Zero()
    {
        var host = new TestHost();
        var pic = host.Pic("Lala");
        var log = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), pic.Id);
        host.Metric(log.Id, null, new DateTime(2026, 9, 3));

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(0, model.TotalViews);
        Assert.Null(model.Pics.Single().ViewsSharePercent); // team has no views -> null, not 0%
    }

    // ============================================================
    // 13) Empty period
    // ============================================================

    [Fact]
    public async Task EmptyPeriod_HasNoData_ButKeepsDenominator()
    {
        var host = new TestHost();
        host.Pic("Maya");

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.False(model.HasData);
        Assert.Equal(0, model.TotalContentCount);
        Assert.Equal(1, model.EligiblePicCount);
        Assert.Single(model.Pics);
        Assert.Null(model.Pics[0].Rank);
        Assert.Null(model.Pics[0].ContentSharePercent);
    }

    // ============================================================
    // 14) Multiple PIC ranking / aggregation
    // ============================================================

    [Fact]
    public async Task MultiplePics_AreRanked_ByViews_ThenContentCount_ThenName()
    {
        var host = new TestHost();
        var rio = host.Pic("Rio");     // 1 content, 500 views
        var ana = host.Pic("Ana");     // 2 content, 500 views (more content -> above Rio)
        var budi = host.Pic("Budi");   // 1 content, 100 views
        host.Pic("Zero");              // no content

        var a1 = host.Log(new DateTime(2026, 9, 2, 9, 0, 0), ana.Id);
        var a2 = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), ana.Id);
        var r1 = host.Log(new DateTime(2026, 9, 2, 11, 0, 0), rio.Id);
        var b1 = host.Log(new DateTime(2026, 9, 2, 12, 0, 0), budi.Id);
        host.Metric(a1.Id, 250, new DateTime(2026, 9, 3));
        host.Metric(a2.Id, 250, new DateTime(2026, 9, 3));
        host.Metric(r1.Id, 500, new DateTime(2026, 9, 3));
        host.Metric(b1.Id, 100, new DateTime(2026, 9, 3));

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(new[] { "Ana", "Rio", "Budi" }, model.Pics.Where(r => r.HasContent).Select(r => r.PicName));
        Assert.Equal(1, model.Pics.Single(r => r.PicName == "Ana").Rank);
        Assert.Equal(2, model.Pics.Single(r => r.PicName == "Rio").Rank);
        Assert.Equal(3, model.Pics.Single(r => r.PicName == "Budi").Rank);
        Assert.Equal(1100, model.TotalViews);
        Assert.Equal(4, model.EligiblePicCount);
        Assert.Equal(3, model.PicsWithContentCount);
        // Shares are rounded to 2 decimals over the team total (content 4, views 1100).
        Assert.Equal(50m, model.Pics.Single(r => r.PicName == "Ana").ContentSharePercent);
        Assert.Equal(45.45m, model.Pics.Single(r => r.PicName == "Rio").ViewsSharePercent);
    }

    [Fact]
    public async Task AverageViewsPerContent_IsNull_When_NoContent()
    {
        var host = new TestHost();
        host.Pic("Idle");

        var model = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Null(model.Pics.Single().AverageViewsPerContent);
    }
}
