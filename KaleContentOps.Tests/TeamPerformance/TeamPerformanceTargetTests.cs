using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Services;
using KaleContentOps.Services.Targets;
using KaleContentOps.Services.TeamPerformance;
using KaleContentOps.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.TeamPerformance;

/// <summary>
/// Phase 4 acceptance tests for the final Team Performance target calculations (business-rule
/// sign-off). Covered, in order:
///  1 target period resolved through the existing TargetService convention,
///  2 NON_KK target included,  3 KK target included,  4 AUTO_GMV_LIVE has no target,
///  5 team target period (sum of daily weekly targets / 7, any range length),
///  6 eligible PIC count is the denominator,  7 zero-content PIC stays in the denominator,
///  8 equal Target Adil for every eligible PIC,  9 Actual counts period uploads only,
/// 10 AUTO_GMV_LIVE excluded from individual Actual, 11 archived included in Actual,
/// 12-14 Belum Diisi has no individual target/selisih/status,
/// 15 Selisih = Actual - Target, 16 positive, 17 negative, 18 zero Selisih,
/// 19 Actual &gt;= Target -> Tercapai, 20 Actual &lt; Target -> Belum Tercapai,
/// 21 Target=0 + Actual=0 -> Tercapai, 22 Target=0 + Actual&gt;0 -> Tercapai,
/// 23 no target configured -> Perlu dilengkapi, 24 partial coverage -> Convention A zeros,
/// 25 employment-period eligibility, 26 no proration for mid-period joiners,
/// 27 multiple PICs aggregate correctly, 28 empty eligible set never divides by zero.
/// All target values come from the REAL TargetService over the same store - no stubbed
/// target engine, no duplicated SCD-2 logic.
/// </summary>
public class TeamPerformanceTargetTests
{
    private const string NonKk = TargetService.NonKkCode; // "NON_KK"
    private const string Kk = TargetService.KkCode;       // "KK"
    private const string AutoGmv = "AUTO_GMV_LIVE";

    private static readonly DateOnly Today = new(2026, 9, 24);
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 7);
    private static readonly DateOnly StartDay = new(2026, 9, 1);
    private static readonly DateOnly EndDay = new(2026, 9, 7);

    private sealed class TestHost
    {
        public AppDbContext Db { get; }
        public TeamPerformanceService Service { get; }
        public TargetService Targets { get; }
        public StubShopTimeZone Clock { get; }
        public int NonKkId { get; }
        public int KkId { get; }
        public int AutoId { get; }

        public TestHost()
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

            NonKkId = Db.ContentTypes.Single(c => c.Code == NonKk).Id;
            KkId = Db.ContentTypes.Single(c => c.Code == Kk).Id;
            AutoId = Db.ContentTypes.Single(c => c.Code == AutoGmv).Id;

            Clock = new StubShopTimeZone(Today);
            Targets = new TargetService(Db, Clock);
            Service = new TeamPerformanceService(Db, Clock, Targets);
        }

        /// <summary>joinDate defaults far in the past (always eligible) so non-eligibility tests stay focused.</summary>
        public MasterPic Pic(string name, bool isActive = true, DateOnly? joinDate = null, DateOnly? resignDate = null)
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

        public ContentLog Log(DateTime? postTime, int? picId, bool isArchived = false, int? contentTypeId = null)
        {
            var log = new ContentLog
            {
                VideoId = $"tp4-{Guid.NewGuid():N}"[..20],
                VideoPostTime = postTime,
                PicId = picId,
                IsArchived = isArchived,
                ContentTypeId = contentTypeId,
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

        /// <summary>Persists a weekly target version through the real TargetService save path.</summary>
        public async Task SeedTarget(int contentTypeId, int upload, DateOnly effectiveDate, long views = 0)
        {
            var result = await Targets.SaveTargetAsync(new TargetSaveRequest
            {
                ContentTypeId = contentTypeId,
                TargetUpload = upload,
                TargetViews = views,
                Today = Today,
                EffectiveDate = effectiveDate
            });
            Assert.True(result.Success, result.ErrorMessage);
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
    // 1-3) Target source and reuse of the existing TargetService convention
    // ============================================================

    [Fact]
    public async Task TargetPeriod_Resolves_Through_Existing_TargetService_Convention()
    {
        var host = new TestHost();
        host.Pic("Ayu");
        await host.SeedTarget(host.NonKkId, 14, StartDay);
        await host.SeedTarget(host.KkId, 21, StartDay);

        var model = await host.BuildAsync(Start, End);

        // Same engine Daily Summary uses - cross-check the service output against it directly.
        var series = await host.Targets.GetDailyTargetSeriesAsync(StartDay, EndDay);
        Assert.Equal(series.NonKk.UploadPeriodTarget + series.Kk.UploadPeriodTarget, model.TeamTargetPeriod);
        Assert.Equal(35m, model.TeamTargetPeriod); // (14 + 21) * 7 days / 7
        Assert.True(model.TargetConfigured);
    }

    [Fact]
    public async Task NonKk_Target_Is_Included_In_TeamTargetPeriod()
    {
        var host = new TestHost();
        host.Pic("Ayu");
        await host.SeedTarget(host.NonKkId, 14, StartDay);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(14m, model.TeamTargetPeriod);
        Assert.True(model.TargetConfigured);
    }

    [Fact]
    public async Task Kk_Target_Is_Included_In_TeamTargetPeriod()
    {
        var host = new TestHost();
        host.Pic("Ayu");
        await host.SeedTarget(host.KkId, 21, StartDay);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(21m, model.TeamTargetPeriod);
        Assert.True(model.TargetConfigured);
    }

    [Fact]
    public async Task AutoGmvLive_Has_No_Target_And_No_Individual_Actual()
    {
        var host = new TestHost();
        var pic = host.Pic("Dadan");

        // The Targets system itself refuses AUTO_GMV_LIVE...
        var rejected = await host.Targets.SaveTargetAsync(new TargetSaveRequest
        {
            ContentTypeId = host.AutoId,
            TargetUpload = 10,
            TargetViews = 1_000,
            Today = Today,
            EffectiveDate = StartDay
        });
        Assert.False(rejected.Success);
        Assert.Equal(TargetSaveErrorCodes.ContentTypeNotTargetable, rejected.ErrorCode);

        // ...so an AUTO-only period has no target at all, and the AUTO upload is workload only.
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), pic.Id, contentTypeId: host.AutoId);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(0m, model.TeamTargetPeriod);
        Assert.False(model.TargetConfigured);
        var row = model.Pics.Single();
        Assert.Equal(0, row.Actual);                    // AUTO never counts individually
        Assert.Equal(1, row.ContentCount);              // but stays in the workload view
        Assert.Equal(1, model.TotalContentCount);       // team total keeps AUTO (rule 14)
        Assert.Null(row.TargetAdil);
        Assert.Equal(TeamPerformanceStatus.Pending, row.Status);
    }

    [Fact]
    public async Task TeamTargetPeriod_Sums_Daily_Weekly_Targets_For_Any_Range_Length()
    {
        var host = new TestHost();
        host.Pic("Ayu");
        await host.SeedTarget(host.NonKkId, 14, StartDay); // 14 uploads / week

        var week = await host.BuildAsync(Start, End);                       // 7 days
        var singleDay = await host.BuildAsync(new DateTime(2026, 9, 3), new DateTime(2026, 9, 3));
        var month = await host.BuildAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));

        Assert.Equal(14m, week.TeamTargetPeriod);   // 14 * 7 / 7
        Assert.Equal(2m, singleDay.TeamTargetPeriod); // 14 / 7 (daily derivation)
        Assert.Equal(60m, month.TeamTargetPeriod);  // 14 * 30 / 7 - arbitrary ranges supported
    }

    // ============================================================
    // 6-8) Denominator and equal Target Adil
    // ============================================================

    [Fact]
    public async Task TargetAdil_Is_TeamTargetPeriod_Over_EligiblePicCount()
    {
        var host = new TestHost();
        host.Pic("Ayu");
        host.Pic("Budi");
        await host.SeedTarget(host.NonKkId, 40, StartDay);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(40m, model.TeamTargetPeriod);
        Assert.Equal(2, model.EligiblePicCount);
        Assert.All(model.Pics, p => Assert.Equal(20m, p.TargetAdil));
    }

    [Fact]
    public async Task ZeroContent_EligiblePic_Keeps_Shared_TargetAdil()
    {
        var host = new TestHost();
        var withContent = host.Pic("Ayu");
        host.Pic("Dian"); // eligible, zero content
        host.Pic("Eka");  // eligible, zero content
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), withContent.Id, contentTypeId: host.NonKkId);
        await host.SeedTarget(host.NonKkId, 30, StartDay);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(3, model.EligiblePicCount);
        Assert.Equal(3, model.Pics.Count);
        Assert.All(model.Pics, p => Assert.Equal(10m, p.TargetAdil)); // 30 / 3 - everyone, not just producers

        var idle = model.Pics.Single(p => p.PicName == "Dian");
        Assert.Equal(0, idle.Actual);
        Assert.Equal(-10m, idle.Selisih);
        Assert.Equal(TeamPerformanceStatus.NotAchieved, idle.Status);
    }

    [Fact]
    public async Task TargetAdil_Is_Equal_For_Every_Eligible_Pic()
    {
        var host = new TestHost();
        var idle = host.Pic("Idle");
        var small = host.Pic("Small");
        var big = host.Pic("Big");
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), small.Id, contentTypeId: host.NonKkId);
        for (var day = 2; day <= 6; day++)
        {
            host.Log(new DateTime(2026, 9, day, 10, 0, 0), big.Id, contentTypeId: host.NonKkId);
        }
        await host.SeedTarget(host.NonKkId, 21, StartDay);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(3, model.EligiblePicCount);
        var targets = model.Pics.Select(p => p.TargetAdil).Distinct().ToList();
        Assert.Single(targets);                       // ONE shared Target Adil value
        Assert.Equal(7m, targets[0]);                 // 21 / 3
        Assert.Equal(new[] { 0, 1, 5 }, model.Pics.Select(p => p.Actual).OrderBy(x => x).ToArray());
        Assert.Contains(model.Pics, p => p.PicName == idle.Name && p.Actual == 0);
    }

    // ============================================================
    // 9-11) Individual Actual
    // ============================================================

    [Fact]
    public async Task Actual_Counts_Only_Uploads_Inside_The_Period()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        host.Log(new DateTime(2026, 8, 31, 12, 0, 0), pic.Id, contentTypeId: host.NonKkId); // before
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);    // in
        host.Log(new DateTime(2026, 9, 7, 23, 59, 0), pic.Id, contentTypeId: host.NonKkId);  // in (last day)
        host.Log(new DateTime(2026, 9, 8, 0, 0, 1), pic.Id, contentTypeId: host.NonKkId);    // after
        host.Log(null, pic.Id, contentTypeId: host.NonKkId);                                 // undated

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(2, row.Actual);
        Assert.Equal(2, row.ContentCount);
    }

    [Fact]
    public async Task AutoGmvLive_Is_Excluded_From_Individual_Actual()
    {
        var host = new TestHost();
        var pic = host.Pic("Dadan");
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), pic.Id, contentTypeId: host.KkId);
        host.Log(new DateTime(2026, 9, 2, 11, 0, 0), pic.Id, contentTypeId: host.AutoId);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(2, row.Actual);       // NON_KK + KK only
        Assert.Equal(3, row.ContentCount); // workload view still sees everything
        Assert.Equal(3, model.TotalContentCount); // team total unchanged (rule 14)
    }

    [Fact]
    public async Task Archived_Content_Is_Included_In_Actual()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id, isArchived: true, contentTypeId: host.NonKkId);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(1, row.Actual); // no archive filter anywhere in Team Performance
        Assert.Equal(1, model.TotalContentCount);
    }

    // ============================================================
    // 12-14) Belum Diisi (PicId NULL) has no individual achievement
    // ============================================================

    [Fact]
    public async Task BelumDiisi_Never_Becomes_A_PicRow_With_Target()
    {
        var host = new TestHost();
        var pic = host.Pic("Budi");
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), null, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 3, 10, 0, 0), null, contentTypeId: host.NonKkId);
        await host.SeedTarget(host.NonKkId, 14, StartDay);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(2, model.UnassignedContentCount);   // counted as team workload
        Assert.Single(model.Pics);                       // ...but never as an individual row
        Assert.All(model.Pics, p => Assert.True(p.PicId > 0));
        Assert.All(model.Pics, p => Assert.True(p.TargetAdil.HasValue)); // target only on real PICs
    }

    [Fact]
    public async Task BelumDiisi_Content_Does_Not_Change_Individual_Selisih()
    {
        var host = new TestHost();
        var pic = host.Pic("Budi");
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), null, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 3, 10, 0, 0), null, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 4, 10, 0, 0), null, contentTypeId: host.NonKkId);
        await host.SeedTarget(host.NonKkId, 14, StartDay);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(1, row.Actual);       // only Budi's own upload
        Assert.Equal(-13m, row.Selisih);   // 1 - 14, NOT 4 - 14
    }

    [Fact]
    public async Task BelumDiisi_Content_Does_Not_Change_Individual_Status()
    {
        var host = new TestHost();
        var pic = host.Pic("Budi");
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        for (var i = 0; i < 4; i++)
        {
            host.Log(new DateTime(2026, 9, 3 + i, 10, 0, 0), null, contentTypeId: host.NonKkId);
        }
        await host.SeedTarget(host.NonKkId, 4, StartDay); // Target Adil = 4

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(1, row.Actual);
        // Team total (5) would say "achieved"; the individual comparison (1 < 4) must win.
        Assert.Equal(5, model.TotalContentCount);
        Assert.Equal(TeamPerformanceStatus.NotAchieved, row.Status);
    }

    // ============================================================
    // 15-20) Selisih and Status
    // ============================================================

    [Fact]
    public async Task Selisih_Equals_Actual_Minus_TargetAdil()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        for (var i = 0; i < 3; i++)
        {
            host.Log(new DateTime(2026, 9, 2 + i, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        }
        await host.SeedTarget(host.NonKkId, 10, StartDay);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(10m, row.TargetAdil);
        Assert.Equal(3, row.Actual);
        Assert.Equal(row.Actual - row.TargetAdil!.Value, row.Selisih);
        Assert.Equal(-7m, row.Selisih); // never absolute value
    }

    [Fact]
    public async Task Selisih_Positive_When_Actual_Above_Target()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        for (var i = 0; i < 10; i++)
        {
            host.Log(new DateTime(2026, 9, 2 + (i % 5), 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        }
        await host.SeedTarget(host.NonKkId, 4, StartDay);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(4m, row.TargetAdil);
        Assert.Equal(10, row.Actual);
        Assert.Equal(6m, row.Selisih);
        Assert.True(row.Selisih > 0);
    }

    [Fact]
    public async Task Selisih_Negative_When_Actual_Below_Target()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        await host.SeedTarget(host.NonKkId, 10, StartDay);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(-9m, row.Selisih);
        Assert.True(row.Selisih < 0);
        Assert.Equal(TeamPerformanceStatus.NotAchieved, row.Status);
    }

    [Fact]
    public async Task Selisih_Zero_When_Actual_Equals_Target()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        for (var i = 0; i < 5; i++)
        {
            host.Log(new DateTime(2026, 9, 2 + i, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        }
        await host.SeedTarget(host.NonKkId, 5, StartDay);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(5m, row.TargetAdil);
        Assert.Equal(0m, row.Selisih); // exactly on target - zero, not +/- epsilon
        Assert.Equal(TeamPerformanceStatus.Achieved, row.Status);
    }

    [Fact]
    public async Task Status_Tercapai_When_Actual_Strictly_Above_Target()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        for (var i = 0; i < 5; i++)
        {
            host.Log(new DateTime(2026, 9, 2 + i, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        }
        await host.SeedTarget(host.NonKkId, 4, StartDay);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(4m, row.TargetAdil);
        Assert.Equal(5, row.Actual);
        Assert.Equal(TeamPerformanceStatus.Achieved, row.Status);
        Assert.Equal("Tercapai", row.Status);
    }

    [Fact]
    public async Task Status_BelumTercapai_When_Actual_Below_Target()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        await host.SeedTarget(host.NonKkId, 10, StartDay);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal("Belum Tercapai", row.Status);
        Assert.Equal(TeamPerformanceStatus.NotAchieved, row.Status);
    }

    // ============================================================
    // 21-23) Target = 0 vs not configured
    // ============================================================

    [Fact]
    public async Task TargetZero_ActualZero_Is_Tercapai()
    {
        var host = new TestHost();
        host.Pic("Rina");
        await host.SeedTarget(host.NonKkId, 0, StartDay); // genuine zero target, configured

        var model = await host.BuildAsync(Start, End);

        Assert.True(model.TargetConfigured);       // configured, NOT "missing"
        var row = model.Pics.Single();
        Assert.Equal(0m, row.TargetAdil);
        Assert.Equal(0, row.Actual);
        Assert.Equal(0m, row.Selisih);
        Assert.Equal(TeamPerformanceStatus.Achieved, row.Status); // 0 >= 0
    }

    [Fact]
    public async Task TargetZero_ActualPositive_Is_Tercapai()
    {
        var host = new TestHost();
        var pic = host.Pic("Rina");
        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 3, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 4, 9, 0, 0), pic.Id, contentTypeId: host.NonKkId);
        await host.SeedTarget(host.NonKkId, 0, StartDay);

        var model = await host.BuildAsync(Start, End);

        var row = model.Pics.Single();
        Assert.Equal(0m, row.TargetAdil);
        Assert.Equal(3, row.Actual);
        Assert.Equal(3m, row.Selisih);
        Assert.Equal(TeamPerformanceStatus.Achieved, row.Status);
    }

    [Fact]
    public async Task NoTargetConfigured_Is_PerluDilengkapi_Never_FakeZero()
    {
        var host = new TestHost();
        host.Pic("Rina");

        var model = await host.BuildAsync(Start, End); // no target rows at all

        Assert.False(model.TargetConfigured);
        Assert.Equal(0m, model.TeamTargetPeriod);
        var row = model.Pics.Single();
        Assert.Null(row.TargetAdil);   // not 0 - missing configuration is never a fake zero
        Assert.False(row.TargetConfigured);
        Assert.Null(row.Selisih);
        Assert.Equal(TeamPerformanceStatus.Pending, row.Status);
        Assert.Equal("Perlu dilengkapi", row.Status);
        Assert.NotEqual(TeamPerformanceStatus.Achieved, row.Status);
    }

    // ============================================================
    // 24) Partial coverage = Convention A (missing days contribute zero)
    // ============================================================

    [Fact]
    public async Task PartialTargetCoverage_MissingDays_ContributeZero()
    {
        var host = new TestHost();
        host.Pic("Rina");
        // Version only from 05 Sep: days 01-04 of the period have no target yet.
        await host.SeedTarget(host.NonKkId, 14, new DateOnly(2026, 9, 5));

        var model = await host.BuildAsync(Start, End);

        Assert.True(model.TargetConfigured);          // EndDate (07 Sep) IS covered
        Assert.Equal(6m, model.TeamTargetPeriod);     // 14 * 3 covered days / 7
        var row = model.Pics.Single();
        Assert.Equal(6m, row.TargetAdil);
        Assert.Equal(TeamPerformanceStatus.NotAchieved, row.Status);
    }

    // ============================================================
    // 25-26) Employment eligibility and no proration
    // ============================================================

    [Fact]
    public async Task EmploymentPeriod_Eligibility_Still_Drives_The_Denominator()
    {
        var host = new TestHost();
        host.Pic("Active", joinDate: new DateOnly(2020, 1, 1));
        host.Pic("ResignedBefore", joinDate: new DateOnly(2020, 1, 1), resignDate: new DateOnly(2026, 8, 31));
        host.Pic("JoinsAfter", joinDate: new DateOnly(2026, 9, 10));
        await host.SeedTarget(host.NonKkId, 14, StartDay);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(1, model.EligiblePicCount);
        var row = Assert.Single(model.Pics);
        Assert.Equal("Active", row.PicName);
        Assert.Equal(14m, row.TargetAdil); // denominator = 1, not 3
    }

    [Fact]
    public async Task NoProration_MidPeriodJoiner_Gets_The_Same_TargetAdil()
    {
        var host = new TestHost();
        host.Pic("Veteran", joinDate: new DateOnly(2020, 1, 1));
        host.Pic("LateJoiner", joinDate: new DateOnly(2026, 9, 4)); // mid-period, still eligible
        await host.SeedTarget(host.NonKkId, 10, StartDay);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(2, model.EligiblePicCount);
        Assert.Equal(5m, model.TeamTargetPeriod / 2);
        var veteran = model.Pics.Single(p => p.PicName == "Veteran");
        var late = model.Pics.Single(p => p.PicName == "LateJoiner");
        Assert.Equal(veteran.TargetAdil, late.TargetAdil); // NO proration for joining on day 4
        Assert.Equal(5m, late.TargetAdil);
    }

    // ============================================================
    // 27-28) Aggregation and the empty denominator
    // ============================================================

    [Fact]
    public async Task MultiplePics_Aggregate_Correctly_With_Auto_And_BelumDiisi()
    {
        var host = new TestHost();
        var ana = host.Pic("Ana");
        var ben = host.Pic("Ben");

        host.Log(new DateTime(2026, 9, 2, 9, 0, 0), ana.Id, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 2, 10, 0, 0), ana.Id, contentTypeId: host.KkId);
        host.Log(new DateTime(2026, 9, 2, 11, 0, 0), ana.Id, contentTypeId: host.AutoId);      // workload only
        host.Log(new DateTime(2026, 9, 3, 9, 0, 0), ben.Id, isArchived: true, contentTypeId: host.NonKkId);
        host.Log(new DateTime(2026, 9, 3, 12, 0, 0), null, contentTypeId: host.NonKkId);       // Belum Diisi
        await host.SeedTarget(host.NonKkId, 8, StartDay);

        var model = await host.BuildAsync(Start, End);

        Assert.Equal(5, model.TotalContentCount);
        Assert.Equal(1, model.UnassignedContentCount);
        Assert.Equal(2, model.EligiblePicCount);
        Assert.Equal(8m, model.TeamTargetPeriod);
        Assert.All(model.Pics, p => Assert.Equal(4m, p.TargetAdil));

        Assert.Equal(2, model.Pics.Single(p => p.PicName == "Ana").Actual);
        Assert.Equal(1, model.Pics.Single(p => p.PicName == "Ben").Actual);
        Assert.Equal(3, model.Pics.Sum(p => p.Actual));
        // Team total deliberately differs from the sum of individual Actuals (AUTO + Belum Diisi):
        Assert.Equal(model.TotalContentCount, model.Pics.Sum(p => p.Actual) + 1 /* AUTO */ + model.UnassignedContentCount);
    }

    [Fact]
    public async Task NoEligiblePics_Never_Divides_By_Zero()
    {
        var host = new TestHost(); // no MasterPic rows at all
        await host.SeedTarget(host.NonKkId, 40, StartDay);

        var model = await host.BuildAsync(Start, End); // must not throw

        Assert.Equal(0, model.EligiblePicCount);
        Assert.Empty(model.Pics);
        Assert.False(model.HasEligiblePics);
        Assert.Equal(40m, model.TeamTargetPeriod); // target still resolved for the period
    }
}

/// <summary>
/// Phase 4 live MVC verification (Step 14): rendered HTML through the real pipeline
/// (WebApplicationFactory + cookie auth), isolated InMemory store per test - the local
/// SQL Server dataset is never written to. Scenarios: configured target, no target,
/// Belum Diisi, zero-content eligible PIC, archived included, AUTO excluded from
/// individual Actual while staying in the team total.
/// </summary>
public class TeamPerformanceTargetUiTests
{
    private const string NonKk = "NON_KK";
    private const string Kk = "KK";
    private const string AutoGmv = "AUTO_GMV_LIVE";

    // Razor HTML-encodes the em dash emitted by Dash() - same constant the ContentLog
    // display tests use (ContentLogUiCleanupTests / ContentLogEnrichedDisplayTests).
    private const string Dash = "&#x2014;";

    private static readonly DateOnly TargetStartDay = new(2026, 9, 1);
    private const string Query = "/TeamPerformance?startDate=2026-09-01&endDate=2026-09-07";

    private static async Task<AuthTestFactory> CreateReadyAsync(string username)
    {
        var factory = new AuthTestFactory();
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        return factory;
    }

    private static async Task<(int NonKk, int Kk, int Auto)> SeedContentTypesAsync(AppDbContext db)
    {
        var nonKk = await db.ContentTypes.FirstOrDefaultAsync(c => c.Code == NonKk);
        if (nonKk is null)
        {
            nonKk = new ContentType { Code = NonKk, Name = "Non-KK" };
            db.ContentTypes.Add(nonKk);
        }

        var kk = await db.ContentTypes.FirstOrDefaultAsync(c => c.Code == Kk);
        if (kk is null)
        {
            kk = new ContentType { Code = Kk, Name = "Keranjang Kuning" };
            db.ContentTypes.Add(kk);
        }

        var auto = await db.ContentTypes.FirstOrDefaultAsync(c => c.Code == AutoGmv);
        if (auto is null)
        {
            auto = new ContentType { Code = AutoGmv, Name = "Auto GMV Live" };
            db.ContentTypes.Add(auto);
        }

        await db.SaveChangesAsync();
        return (nonKk.Id, kk.Id, auto.Id);
    }

    private static MasterPic AddPic(AppDbContext db, string name)
    {
        var pic = new MasterPic { Name = name, IsActive = true, JoinDate = new DateOnly(2020, 1, 1) };
        db.MasterPics.Add(pic);
        db.SaveChanges();
        return pic;
    }

    private static ContentLog AddLog(AppDbContext db, int? picId, DateTime postTime, int? contentTypeId, bool archived = false)
    {
        var log = new ContentLog
        {
            VideoId = $"tp4ui-{Guid.NewGuid():N}"[..20],
            VideoPostTime = postTime,
            PicId = picId,
            ContentTypeId = contentTypeId,
            IsArchived = archived
        };
        db.ContentLogs.Add(log);
        db.SaveChanges();
        return log;
    }

    private static async Task SeedUploadTargetAsync(AppDbContext db, IShopTimeZone clock, int contentTypeId, int upload)
    {
        var targets = new TargetService(db, clock);
        var result = await targets.SaveTargetAsync(new TargetSaveRequest
        {
            ContentTypeId = contentTypeId,
            TargetUpload = upload,
            TargetViews = 0,
            Today = clock.Today(),
            EffectiveDate = TargetStartDay
        });
        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task ConfiguredTarget_Renders_NumericTarget_SignedSelisih_And_Status()
    {
        const string user = "tp4.ui.configured";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var types = await SeedContentTypesAsync(db);
                var pic = AddPic(db, "Dina");
                AddLog(db, pic.Id, new DateTime(2026, 9, 3, 10, 0, 0), types.NonKk);
                AddLog(db, pic.Id, new DateTime(2026, 9, 4, 10, 0, 0), types.NonKk);
                var clock = scope.ServiceProvider.GetRequiredService<IShopTimeZone>();
                await SeedUploadTargetAsync(db, clock, types.NonKk, upload: 4);
            }

            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync(Query);

            // Team Target Period 4 / 1 eligible PIC -> Target Adil 4; Actual 2 -> Selisih -2.
            Assert.Contains("<td class=\"tp-col-num tp-col-upload\">2</td>", html);
            Assert.Contains("<td class=\"tp-col-num tp-col-target\">4</td>", html);
            Assert.Contains("<td class=\"tp-col-num tp-col-selisih\">-2</td>", html);
            Assert.Contains(">Belum Tercapai<", html);
            Assert.Contains("tp-badge-danger", html);
            Assert.DoesNotContain("tp-badge-pending", html); // no neutral state when target exists
        }
    }

    [Fact]
    public async Task NoTargetConfigured_Renders_Dashes_And_PerluDilengkapi()
    {
        const string user = "tp4.ui.notarget";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var types = await SeedContentTypesAsync(db);
                var pic = AddPic(db, "Dina");
                AddLog(db, pic.Id, new DateTime(2026, 9, 3, 10, 0, 0), types.NonKk);
                // no target seeded at all
            }

            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync(Query);

            Assert.Contains($"<td class=\"tp-col-num tp-col-target\">{Dash}</td>", html);
            Assert.Contains($"<td class=\"tp-col-num tp-col-selisih\">{Dash}</td>", html);
            Assert.Contains("<span class=\"tp-badge tp-badge-pending\">Perlu dilengkapi</span>", html);
            Assert.DoesNotContain("Belum Tercapai", html);
            Assert.DoesNotContain("tp-badge-danger", html);
            Assert.DoesNotContain("tp-badge-success", html);
        }
    }

    [Fact]
    public async Task BelumDiisi_Row_Shows_Dashes_And_Neutral_State()
    {
        const string user = "tp4.ui.belumdiisi";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var types = await SeedContentTypesAsync(db);
                var pic = AddPic(db, "Budi");
                AddLog(db, pic.Id, new DateTime(2026, 9, 3, 10, 0, 0), types.NonKk);
                AddLog(db, null, new DateTime(2026, 9, 4, 10, 0, 0), types.NonKk);
                AddLog(db, null, new DateTime(2026, 9, 5, 10, 0, 0), types.NonKk);
                var clock = scope.ServiceProvider.GetRequiredService<IShopTimeZone>();
                await SeedUploadTargetAsync(db, clock, types.NonKk, upload: 4);
            }

            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync(Query);

            // Individual row: Actual 1, Target 4, Selisih -3, Belum Tercapai.
            Assert.Contains("<td class=\"tp-col-num tp-col-selisih\">-3</td>", html);

            var rowStart = html.IndexOf("tp-row-unassigned", StringComparison.Ordinal);
            Assert.True(rowStart >= 0, "Belum Diisi row must render");
            var segment = html.Substring(rowStart, Math.Min(700, html.Length - rowStart));
            var dashCount = segment.Split(new[] { $">{Dash}<" }, StringSplitOptions.None).Length - 1;
            Assert.True(dashCount >= 2, $"Belum Diisi row must show dashes for Target and Selisih (found {dashCount})");
            Assert.Contains("Perlu dilengkapi", segment); // neutral state, no individual status
        }
    }

    [Fact]
    public async Task ZeroContentPic_Shows_Target_ZeroActual_NegativeSelisih()
    {
        const string user = "tp4.ui.zeroccontent";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var types = await SeedContentTypesAsync(db);
                AddPic(db, "Ada");
                AddPic(db, "Zoe"); // eligible, zero content
                var ada = db.MasterPics.Single(p => p.Name == "Ada");
                AddLog(db, ada.Id, new DateTime(2026, 9, 3, 10, 0, 0), types.NonKk);
                var clock = scope.ServiceProvider.GetRequiredService<IShopTimeZone>();
                await SeedUploadTargetAsync(db, clock, types.NonKk, upload: 4); // 4 / 2 PICs = 2
            }

            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync(Query);

            Assert.Contains("Zoe", html);                                                    // row still present
            Assert.Contains($"<td class=\"tp-col-num tp-col-upload\">0</td>", html);          // Actual 0
            Assert.Contains($"<td class=\"tp-col-num tp-col-selisih\">-2</td>", html);        // 0 - 2
            Assert.Contains(">Belum Tercapai<", html);
        }
    }

    [Fact]
    public async Task ArchivedContent_Counts_In_Rendered_Actual()
    {
        const string user = "tp4.ui.archived";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var types = await SeedContentTypesAsync(db);
                var pic = AddPic(db, "Rina");
                AddLog(db, pic.Id, new DateTime(2026, 9, 3, 10, 0, 0), types.NonKk, archived: true);
                var clock = scope.ServiceProvider.GetRequiredService<IShopTimeZone>();
                await SeedUploadTargetAsync(db, clock, types.NonKk, upload: 4);
            }

            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync(Query);

            Assert.Contains($"<td class=\"tp-col-num tp-col-upload\">1</td>", html); // archived included
            Assert.Contains(">Belum Tercapai<", html);                               // 1 < 4
        }
    }

    [Fact]
    public async Task AutoGmvLive_Does_Not_Increase_Individual_Actual_But_Stays_In_TeamTotal()
    {
        const string user = "tp4.ui.auto";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var types = await SeedContentTypesAsync(db);
                var pic = AddPic(db, "Dadan");
                AddLog(db, pic.Id, new DateTime(2026, 9, 3, 10, 0, 0), types.Auto);
                var clock = scope.ServiceProvider.GetRequiredService<IShopTimeZone>();
                await SeedUploadTargetAsync(db, clock, types.NonKk, upload: 4);
            }

            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync(Query);

            Assert.Contains($"<td class=\"tp-col-num tp-col-upload\">0</td>", html); // individual Actual 0
            Assert.Contains("<td class=\"tp-col-num tp-col-target\">4</td>", html);
            Assert.Contains($"<td class=\"tp-col-num tp-col-selisih\">-4</td>", html); // 0 - 4
            Assert.Contains(">Belum Tercapai<", html);
            Assert.Contains("<div class=\"tp-summary-value\">1</div>", html);          // team total keeps AUTO
        }
    }
}
