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
/// Phase 3A acceptance tests: employment-period eligibility for Team Performance.
///
/// LOCKED RULE under test - a PIC is eligible for the inclusive reporting period when:
///     JoinDate &lt;= EndDate AND (ResignDate IS NULL OR ResignDate &gt;= StartDate)
///
/// Proven here: exact boundary conditions (JoinDate/ResignDate equal to StartDate/EndDate,
/// one day outside), that current IsActive is NOT the historical eligibility filter,
/// that eligible zero-content PICs stay in the denominator while ineligible PICs never get
/// a row, that archived content and PicId=NULL behaviour are unchanged, that aggregation
/// still works after filtering, and the Phase 2B lifecycle compatibility case
/// (deactivated on day D - eligible through D, not eligible from D+1).
/// </summary>
public class TeamPerformanceEmploymentPeriodTests
{
    // Inclusive reporting period used by every test unless stated otherwise.
    private static readonly DateTime PeriodStart = new(2026, 9, 1);
    private static readonly DateTime PeriodEnd = new(2026, 9, 7);

    // DateOnly twins of the reporting period (MasterPic employment dates are DateOnly).
    private static readonly DateOnly PeriodStartDay = new(2026, 9, 1);
    private static readonly DateOnly PeriodEndDay = new(2026, 9, 7);

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
            // Phase 4: TeamPerformanceService now also takes ITargetService (real TargetService
            // over the same store). Eligibility assertions below are unchanged.
            var clock = new StubShopTimeZone(new DateOnly(2026, 9, 24));
            Service = new TeamPerformanceService(Db, clock, new TargetService(Db, clock));
        }

        /// <summary>
        /// Seeds a MasterPIC with an explicit employment period. joinDate defaults far in
        /// the past (always eligible) so tests that are NOT about eligibility stay focused;
        /// eligibility tests always pass join/resign explicitly.
        /// </summary>
        public MasterPic Pic(
            string name,
            bool isActive = true,
            DateOnly? joinDate = null,
            DateOnly? resignDate = null)
        {
            var pic = new MasterPic
            {
                Name = name,
                IsActive = isActive,
                JoinDate = joinDate ?? new DateOnly(2020, 1, 1),
                ResignDate = resignDate
            };
            Db.MasterPics.Add(pic);
            Db.SaveChanges();
            return pic;
        }

        public ContentLog Log(DateTime? postTime, int? picId, bool isArchived = false)
        {
            var log = new ContentLog
            {
                VideoId = $"tpep-{Guid.NewGuid():N}"[..20],
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

    // ==================================================================
    // 1/2/3) JoinDate boundaries
    // ==================================================================

    [Fact]
    public async Task JoinDate_Before_Period_Is_Eligible()
    {
        var host = new TestHost();
        host.Pic("Aldi", joinDate: new DateOnly(2026, 1, 1));

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(1, model.EligiblePicCount);
        Assert.Single(model.Pics);
        Assert.Equal("Aldi", model.Pics[0].PicName);
    }

    [Fact]
    public async Task JoinDate_Exactly_Equal_To_EndDate_Is_Eligible()
    {
        var host = new TestHost();
        host.Pic("Bela", joinDate: PeriodEndDay);            // 2026-09-07 == EndDate
        host.Pic("Citra", joinDate: PeriodEndDay.AddDays(1)); // 2026-09-08 -> after EndDate

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(1, model.EligiblePicCount);
        Assert.Equal("Bela", Assert.Single(model.Pics).PicName);
    }

    [Fact]
    public async Task JoinDate_After_EndDate_Is_Excluded()
    {
        var host = new TestHost();
        host.Pic("Doni", joinDate: PeriodEndDay.AddDays(1));

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(0, model.EligiblePicCount);
        Assert.Empty(model.Pics);
    }

    // ==================================================================
    // 4/5/6/7) ResignDate boundaries
    // ==================================================================

    [Fact]
    public async Task ResignDate_Null_Remains_Eligible()
    {
        var host = new TestHost();
        host.Pic("Eka", isActive: false, joinDate: new DateOnly(2026, 2, 1), resignDate: null);

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(1, model.EligiblePicCount);
        Assert.Single(model.Pics);
    }

    [Fact]
    public async Task ResignDate_Exactly_Equal_To_StartDate_Is_Eligible()
    {
        var host = new TestHost();
        host.Pic("Fajar", isActive: false,
            joinDate: new DateOnly(2026, 1, 1), resignDate: PeriodStartDay); // 2026-09-01 == StartDate

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(1, model.EligiblePicCount);
        Assert.Single(model.Pics);
    }

    [Fact]
    public async Task ResignDate_Exactly_Equal_To_EndDate_Is_Eligible()
    {
        var host = new TestHost();
        host.Pic("Gani", isActive: false,
            joinDate: new DateOnly(2026, 1, 1), resignDate: PeriodEndDay); // 2026-09-07 == EndDate

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(1, model.EligiblePicCount);
        Assert.Single(model.Pics);
    }

    [Fact]
    public async Task ResignDate_Before_StartDate_Is_Excluded()
    {
        var host = new TestHost();
        host.Pic("Hana", isActive: false,
            joinDate: new DateOnly(2026, 1, 1), resignDate: PeriodStartDay.AddDays(-1)); // 2026-08-31

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(0, model.EligiblePicCount);
        Assert.Empty(model.Pics);
    }

    // ==================================================================
    // 8/9/10) IsActive is NOT the historical eligibility filter
    // ==================================================================

    [Fact]
    public async Task Inactive_Pic_With_Overlapping_Historical_Period_Is_Included()
    {
        // Locked example: Join 2026-01-01, Resign 2026-06-30, IsActive=false.
        // May 2026 report -> MUST be eligible.
        var host = new TestHost();
        host.Pic("Indah", isActive: false,
            joinDate: new DateOnly(2026, 1, 1), resignDate: new DateOnly(2026, 6, 30));

        var model = await host.BuildAsync(new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        Assert.Equal(1, model.EligiblePicCount);
        Assert.Single(model.Pics);
    }

    [Fact]
    public async Task Inactive_Pic_With_NonOverlapping_Period_Is_Excluded()
    {
        // Same PIC: July 2026 report (after ResignDate) -> MUST NOT be eligible.
        var host = new TestHost();
        host.Pic("Indah", isActive: false,
            joinDate: new DateOnly(2026, 1, 1), resignDate: new DateOnly(2026, 6, 30));

        var model = await host.BuildAsync(new DateTime(2026, 7, 1), new DateTime(2026, 7, 31));

        Assert.Equal(0, model.EligiblePicCount);
        Assert.Empty(model.Pics);
    }

    [Fact]
    public async Task Active_Pic_With_NonOverlapping_JoinDate_Is_Excluded()
    {
        // IsActive=true alone must never qualify a PIC for a period it did not work in.
        var host = new TestHost();
        host.Pic("Joko", isActive: true, joinDate: PeriodEndDay.AddDays(3));

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(0, model.EligiblePicCount);
        Assert.Empty(model.Pics);
    }

    [Fact]
    public async Task Active_Pic_With_ResignDate_Before_StartDate_Is_Excluded()
    {
        var host = new TestHost();
        host.Pic("Kartika", isActive: true,
            joinDate: new DateOnly(2026, 1, 1), resignDate: PeriodStartDay.AddDays(-1));

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(0, model.EligiblePicCount);
        Assert.Empty(model.Pics);
    }

    // ==================================================================
    // 11/12) Denominator: eligible zero-content stays, ineligible never appears
    // ==================================================================

    [Fact]
    public async Task Eligible_Pic_With_Zero_Content_Remains_In_Denominator()
    {
        var host = new TestHost();
        var worker = host.Pic("Lina", joinDate: new DateOnly(2026, 8, 20));
        host.Pic("Miko", joinDate: new DateOnly(2026, 9, 7), resignDate: null); // eligible, silent period
        var log = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), worker.Id);
        host.Metric(log.Id, 500, new DateTime(2026, 9, 3));

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(2, model.EligiblePicCount);
        Assert.Equal(2, model.Pics.Count);
        var silent = model.Pics.Single(r => r.PicName == "Miko");
        Assert.Equal(0, silent.ContentCount);
        Assert.Equal(0, silent.TotalViews);
        Assert.Null(silent.Rank);
        Assert.Null(silent.AverageViewsPerContent);
    }

    [Fact]
    public async Task Ineligible_Pic_With_Content_Does_Not_Appear()
    {
        var host = new TestHost();
        var eligible = host.Pic("Nina", joinDate: new DateOnly(2026, 1, 1));
        var ineligible = host.Pic("Oprek", isActive: false,
            joinDate: new DateOnly(2026, 1, 1), resignDate: PeriodStartDay.AddDays(-1));

        // Ineligible PIC has content inside the period: no row may be produced for it.
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), eligible.Id);
        host.Log(new DateTime(2026, 9, 3, 10, 0, 0), ineligible.Id);

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(1, model.EligiblePicCount);
        var row = Assert.Single(model.Pics);
        Assert.Equal("Nina", row.PicName);
        Assert.DoesNotContain(model.Pics, r => r.PicName == "Oprek");
        // Team totals keep the established behaviour (ALL period content counts - locked rule 1
        // and existing aggregation are preserved; only the per-PIC rows follow eligibility).
        Assert.Equal(2, model.TotalContentCount);
        Assert.Equal(2, model.AssignedContentCount);
    }

    // ==================================================================
    // 13/14) Archived content and PicId NULL under the eligibility filter
    // ==================================================================

    [Fact]
    public async Task Archived_Content_Is_Included_For_Eligible_Pic()
    {
        var host = new TestHost();
        var pic = host.Pic("Putri", joinDate: new DateOnly(2026, 3, 1));
        var live = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), pic.Id, isArchived: false);
        var archived = host.Log(new DateTime(2026, 9, 2, 11, 0, 0), pic.Id, isArchived: true);
        host.Metric(live.Id, 100, new DateTime(2026, 9, 3));
        host.Metric(archived.Id, 40, new DateTime(2026, 9, 3));

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(2, model.TotalContentCount);
        Assert.Equal(140, model.TotalViews);
        Assert.Equal(2, model.Pics.Single().ContentCount);
    }

    [Fact]
    public async Task Null_PicId_Is_Not_A_Pic_And_Does_Not_Affect_Eligibility()
    {
        var host = new TestHost();
        var pic = host.Pic("Qori", joinDate: new DateOnly(2026, 6, 1));
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), pic.Id);
        host.Log(new DateTime(2026, 9, 2, 11, 0, 0), null); // "Belum Diisi"

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(1, model.EligiblePicCount);
        var row = Assert.Single(model.Pics);
        Assert.Equal("Qori", row.PicName);
        Assert.Equal(1, model.UnassignedContentCount); // team bucket only, never a PIC row
        Assert.Equal(2, model.TotalContentCount);
    }

    // ==================================================================
    // 15) Aggregation after eligibility filtering
    // ==================================================================

    [Fact]
    public async Task Multiple_Pics_Are_Aggregated_Correctly_After_Eligibility_Filtering()
    {
        var host = new TestHost();
        var a = host.Pic("Rani", joinDate: new DateOnly(2026, 1, 1));                       // eligible, 2 content
        host.Pic("Sari", joinDate: PeriodEndDay);                                            // eligible, 0 content
        var c = host.Pic("Tomi", isActive: false,
            joinDate: new DateOnly(2026, 1, 1), resignDate: PeriodStartDay.AddDays(-1));     // ineligible, 1 content

        var a1 = host.Log(new DateTime(2026, 9, 2, 9, 0, 0), a.Id);
        var a2 = host.Log(new DateTime(2026, 9, 3, 9, 0, 0), a.Id);
        host.Log(new DateTime(2026, 9, 4, 9, 0, 0), c.Id);
        host.Metric(a1.Id, 300, new DateTime(2026, 9, 3));
        host.Metric(a2.Id, 700, new DateTime(2026, 9, 3));

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.Equal(2, model.EligiblePicCount);
        Assert.Equal(1, model.PicsWithContentCount);
        Assert.Equal(2, model.Pics.Count);

        var rani = model.Pics.Single(r => r.PicName == "Rani");
        Assert.Equal(1, rani.Rank);
        Assert.Equal(2, rani.ContentCount);
        Assert.Equal(1000, rani.TotalViews);
        Assert.Equal(500m, rani.AverageViewsPerContent); // 1000 views / 2 content

        var sari = model.Pics.Single(r => r.PicName == "Sari");
        Assert.Equal(0, sari.ContentCount);
        Assert.Null(sari.Rank);

        Assert.DoesNotContain(model.Pics, r => r.PicName == "Tomi");
        Assert.Equal(3, model.TotalContentCount);   // existing totals behaviour preserved
        Assert.Equal(1000, model.TotalViews);
    }

    // ==================================================================
    // Step 8 - Phase 2B lifecycle compatibility
    // ==================================================================

    [Fact]
    public async Task Pic_Deactivated_On_D_Is_Eligible_Through_D_And_Excluded_From_D_Plus_1()
    {
        // Phase 2B: an active PIC deactivated on 2026-10-05 gets ResignDate = 2026-10-05.
        var host = new TestHost();
        host.Pic("Udin", isActive: false,
            joinDate: new DateOnly(2026, 2, 1), resignDate: new DateOnly(2026, 10, 5));

        // Report ENDING 2026-10-05 -> eligible (ResignDate >= StartDate).
        var endingOnResign = await host.BuildAsync(new DateTime(2026, 9, 29), new DateTime(2026, 10, 5));
        Assert.Equal(1, endingOnResign.EligiblePicCount);
        Assert.Single(endingOnResign.Pics);

        // Report STARTING 2026-10-06 -> not eligible (ResignDate < StartDate).
        var startingAfterResign = await host.BuildAsync(new DateTime(2026, 10, 6), new DateTime(2026, 10, 12));
        Assert.Equal(0, startingAfterResign.EligiblePicCount);
        Assert.Empty(startingAfterResign.Pics);
    }

    [Fact]
    public async Task EmploymentPeriodData_Is_Reported_Available_With_Real_Filter()
    {
        var host = new TestHost();
        host.Pic("Vina", joinDate: new DateOnly(2026, 4, 1));

        var model = await host.BuildAsync(PeriodStart, PeriodEnd);

        Assert.True(model.EmploymentPeriodDataAvailable);
    }
}
