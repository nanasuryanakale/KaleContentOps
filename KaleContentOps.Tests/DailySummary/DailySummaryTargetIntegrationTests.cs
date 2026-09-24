using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using KaleContentOps.Services.DailySummary;
using KaleContentOps.Services.Targets;
using Xunit;

namespace KaleContentOps.Tests.DailySummary;

/// <summary>
/// Phase 6 tests: Daily Summary targets come from Menu Targets (TargetService) as the
/// single source of truth - no appsettings, no hard-coded values. Covers:
/// - weekly -> daily derivation (docs/daily-summary-spec.md sections 10 and 12),
/// - per-content-type resolution: each type's Upload AND Views come from the SAME
///   target version of THAT type (UAT fix - views were previously taken from KK),
/// - per-date SCD-2 resolution across an Effective Date change (historical dates keep
///   their own version; future versions never match before their EffectiveFrom),
/// - days before the first version get zeros (missing-target convention),
/// - exact period target (raw weekly sum / 7, not the sum of rounded dailies),
/// - DailySummaryService integration (row statuses, TOTAL targets, Pencapaian vs Target),
/// - Ringkasan actuals and Composition percentages are unaffected by target integration.
/// EF InMemory + stub clock, same pattern as Targets/TargetActualsPhase5Tests.
/// </summary>
public class DailySummaryTargetIntegrationTests
{
    private const string NonKk = TargetService.NonKkCode; // "NON_KK"
    private const string Kk = TargetService.KkCode;       // "KK"

    private sealed class TestHost
    {
        public string DatabaseName { get; } = Guid.NewGuid().ToString();
        public AppDbContext Db { get; }
        public TargetService TargetService { get; }
        public DailySummaryService DailySummaryService { get; }
        public int NonKkId { get; }
        public int KkId { get; }
        public int AutoGmvId { get; }

        public TestHost()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(DatabaseName)
                .Options;
            Db = new AppDbContext(options);

            Db.ContentTypes.AddRange(
                new ContentType { Code = NonKk, Name = "Non-KK" },
                new ContentType { Code = Kk, Name = "Keranjang Kuning" },
                new ContentType { Code = "AUTO_GMV_LIVE", Name = "Auto GMV Live" });
            Db.SaveChanges();

            NonKkId = Db.ContentTypes.Single(c => c.Code == NonKk).Id;
            KkId = Db.ContentTypes.Single(c => c.Code == Kk).Id;
            AutoGmvId = Db.ContentTypes.Single(c => c.Code == "AUTO_GMV_LIVE").Id;

            var timeZone = new StubShopTimeZone(new DateOnly(2026, 9, 24));
            TargetService = new TargetService(Db, timeZone);
            DailySummaryService = new DailySummaryService(Db, TargetService);
        }

        public Target WeeklyTarget(int contentTypeId, int upload, long views, DateOnly from, DateOnly? to = null) =>
            new()
            {
                ContentTypeId = contentTypeId,
                TargetUpload = upload,
                TargetViews = views,
                EffectiveFrom = from,
                EffectiveTo = to,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

        public ContentLog Log(DateTime postTime, int contentTypeId) =>
            new()
            {
                VideoId = $"vid-{Guid.NewGuid():N}",
                VideoPostTime = postTime,
                ContentTypeId = contentTypeId,
                IsArchived = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

        public ContentMetric Metric(long contentLogId, long views, DateTime capturedAt) =>
            new()
            {
                ContentLogId = contentLogId,
                Views = views,
                CapturedAt = capturedAt
            };

        public void Save() => Db.SaveChanges();
    }

    /// <summary>Injectable server clock (Asia/Jakarta), same pattern as Targets tests.</summary>
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
    // GetDailyTargetSeriesAsync - weekly -> daily derivation
    // ============================================================

    [Fact]
    public async Task DailySeries_SingleVersion_DividesWeeklyTargetBySeven()
    {
        var host = new TestHost();
        // Spec sections 10/12 beta values: NON_KK 21 content/week, KK 105,000 views/week.
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 21, views: 0, from: new DateOnly(2026, 8, 1)));
        host.Db.Targets.Add(host.WeeklyTarget(host.KkId, upload: 0, views: 105_000, from: new DateOnly(2026, 8, 1)));
        host.Save();

        var series = await host.TargetService.GetDailyTargetSeriesAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 7), CancellationToken.None);

        Assert.All(Enumerable.Range(0, 7), i => Assert.Equal(3m, series.NonKk.UploadByDayIndex(i)));    // 21/7
        Assert.All(Enumerable.Range(0, 7), i => Assert.Equal(15_000m, series.Kk.ViewsByDayIndex(i)));   // 105,000/7
        Assert.Equal(21m, series.NonKk.UploadPeriodTarget);                                             // 7-day period = one week
        Assert.Equal(105_000m, series.Kk.ViewsPeriodTarget);
    }

    [Fact]
    public async Task DailySeries_UploadAndViews_ComeFromTheSameVersionOfTheSameType()
    {
        var host = new TestHost();
        // NON_KK version carries BOTH values: 13/wk upload and 26,000/wk views.
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 13, views: 26_000, from: new DateOnly(2026, 9, 1)));
        host.Save();

        var series = await host.TargetService.GetDailyTargetSeriesAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 7), CancellationToken.None);

        Assert.All(Enumerable.Range(0, 7), i =>
        {
            Assert.Equal(1.8571m, series.NonKk.UploadByDayIndex(i));        // 13/7 rounded to 4dp - NON_KK version
            Assert.Equal(3_714.2857m, series.NonKk.ViewsByDayIndex(i));     // 26,000/7 - SAME version, NOT KK views
        });
        Assert.Equal(13m, series.NonKk.UploadPeriodTarget);
        Assert.Equal(26_000m, series.NonKk.ViewsPeriodTarget);
    }

    [Fact]
    public async Task DailySeries_EffectiveDateChange_ResolvesEachDateByItsOwnVersion()
    {
        var host = new TestHost();
        // 01 Sep -> 10/week; 10 Sep -> 35/week (same-day trim rule mirrored in the seed).
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 10, views: 0,
            from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 9)));
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 35, views: 0,
            from: new DateOnly(2026, 9, 10)));
        host.Save();

        var series = await host.TargetService.GetDailyTargetSeriesAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), CancellationToken.None);

        Assert.Equal(1.4286m, series.NonKk.UploadByDayIndex(0));               // Sep 1: 10/7
        Assert.Equal(1.4286m, series.NonKk.UploadByDayIndex(8));               // Sep 9: still the old version
        Assert.Equal(5m, series.NonKk.UploadByDayIndex(9));                    // Sep 10: new version 35/7
        Assert.Equal(5m, series.NonKk.UploadByDayIndex(29));                   // Sep 30
        // Exact period: (10*9 + 35*21)/7 = 825/7, never 9*1.4286 + 21*5.
        Assert.Equal(117.8571m, series.NonKk.UploadPeriodTarget);
    }

    [Fact]
    public async Task DailySeries_FutureVersion_NeverUsedBeforeItsEffectiveFrom()
    {
        var host = new TestHost();
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 21, views: 0,
            from: new DateOnly(2026, 10, 1))); // scheduled version
        host.Save();

        var series = await host.TargetService.GetDailyTargetSeriesAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), CancellationToken.None);

        Assert.All(Enumerable.Range(0, 30), i => Assert.Equal(0m, series.NonKk.UploadByDayIndex(i)));
        Assert.Equal(0m, series.NonKk.UploadPeriodTarget);
    }

    [Fact]
    public async Task DailySeries_DaysBeforeFirstVersion_GetZeros()
    {
        var host = new TestHost();
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 14, views: 0,
            from: new DateOnly(2026, 10, 1)));
        host.Save();

        var series = await host.TargetService.GetDailyTargetSeriesAsync(
            new DateOnly(2026, 9, 25), new DateOnly(2026, 10, 3), CancellationToken.None);

        Assert.Equal(0m, series.NonKk.UploadByDayIndex(0));                    // Sep 25: no version yet
        Assert.Equal(0m, series.NonKk.UploadByDayIndex(5));                    // Sep 30
        Assert.Equal(2m, series.NonKk.UploadByDayIndex(6));                    // Oct 1: 14/7
        Assert.Equal(2m, series.NonKk.UploadByDayIndex(8));                    // Oct 3
        Assert.Equal(6m, series.NonKk.UploadPeriodTarget);                     // (14*3)/7
    }

    [Fact]
    public async Task DailySeries_PeriodTarget_IsExactDivision_NotSumOfRoundedDailies()
    {
        var host = new TestHost();
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 22, views: 0,
            from: new DateOnly(2026, 9, 1))); // 22/7 = 3.142857... rounds to 3.1429
        host.Save();

        var series = await host.TargetService.GetDailyTargetSeriesAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 7), CancellationToken.None);

        Assert.Equal(3.1429m, series.NonKk.UploadByDayIndex(0));
        // 7 * 3.1429 = 22.0003 - wrong; the exact value is 22.
        Assert.Equal(22m, series.NonKk.UploadPeriodTarget);
    }

    // ============================================================
    // UAT regression (Phase 6 manual UAT finding): NON-KK Target Views
    // must come from the NON_KK versions (82,357), never from KK (643).
    // ============================================================

    [Fact]
    public async Task DailySeries_UatRegression_NonKkViewsFromNonKkVersions_82357_Not643()
    {
        var host = new TestHost();
        // Exact UAT scenario, range 01-30 Sep 2026:
        // NON_KK: 01 Sep -> 13/wk, 26,000 views; 23 Sep -> 3/wk, 500 views; 30 Sep -> 5/wk, 1,000 views.
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 13, views: 26_000,
            from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 22)));
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 3, views: 500,
            from: new DateOnly(2026, 9, 23), to: new DateOnly(2026, 9, 29)));
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 5, views: 1_000,
            from: new DateOnly(2026, 9, 30)));
        // KK: only late-month versions with small views (the source of the buggy 643).
        host.Db.Targets.Add(host.WeeklyTarget(host.KkId, upload: 1, views: 500,
            from: new DateOnly(2026, 9, 23), to: new DateOnly(2026, 9, 29)));
        host.Db.Targets.Add(host.WeeklyTarget(host.KkId, upload: 1, views: 1_000,
            from: new DateOnly(2026, 9, 30)));
        host.Save();

        var series = await host.TargetService.GetDailyTargetSeriesAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), CancellationToken.None);

        // NON_KK Target Upload: (13*22 + 3*7 + 5*1)/7 = 312/7 = 44.5714 (~45 in the UI).
        Assert.Equal(44.5714m, series.NonKk.UploadPeriodTarget);
        // NON_KK Target Views: (26,000*22 + 500*7 + 1,000*1)/7 = 576,500/7 = 82,357.1429.
        // The bug returned 642.8571 (KK views). Regression guard: must stay ~82,357.
        Assert.Equal(82_357.1429m, series.NonKk.ViewsPeriodTarget);
        Assert.True(series.NonKk.ViewsPeriodTarget > 10_000m, "NON-KK views target must never collapse to the KK value (~643).");

        // KK Target Views: (500*7 + 1,000*1)/7 = 4,500/7 = 642.8571 (~643 in the UI).
        Assert.Equal(642.8571m, series.Kk.ViewsPeriodTarget);
        // KK Target Upload: (1*7 + 1*1)/7 = 8/7 = 1.1429.
        Assert.Equal(1.1429m, series.Kk.UploadPeriodTarget);

        // Per-date spot checks: Sep 1 uses the 01 Sep version, Sep 23 the middle one.
        Assert.Equal(3_714.2857m, series.NonKk.ViewsByDayIndex(0));  // 26,000/7 (4dp)
        Assert.Equal(71.4286m, series.NonKk.ViewsByDayIndex(22));    // 500/7 (4dp)
        Assert.Equal(142.8571m, series.NonKk.ViewsByDayIndex(29));   // 1,000/7 (4dp)
    }

    // ============================================================
    // DailySummaryService integration - TargetService as source of truth
    // ============================================================

    private static DailySummaryFilter Filter(DateTime start, DateTime end, bool includeAutoGmv = true) =>
        new()
        {
            StartDate = start,
            EndDate = end,
            IncludeArchived = false,
            IncludeAutoGmvInTotal = includeAutoGmv,
            ShowNonKkColumns = true,
            ShowKkColumns = true,
            ShowAutoGmvColumns = true
        };

    [Fact]
    public async Task BuildAsync_RowStatusesAndTotalTargets_ComeFromTargetService()
    {
        var host = new TestHost();
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 21, views: 0, from: new DateOnly(2026, 8, 1)));      // 3 content/day
        host.Db.Targets.Add(host.WeeklyTarget(host.KkId, upload: 105, views: 105_000, from: new DateOnly(2026, 8, 1))); // 15/day, 15,000 views/day
        host.Save();

        // Sep 1: 3 NON_KK logs at 15,001 views each -> all three NON_KK metrics at/above target.
        for (var i = 0; i < 3; i++)
        {
            var log = host.Log(new DateTime(2026, 9, 1, 10, 0, 0), host.NonKkId);
            host.Db.ContentLogs.Add(log);
            host.Db.ContentMetrics.Add(host.Metric(log.Id, 15_001, new DateTime(2026, 9, 2, 0, i, 0)));
        }
        // Sep 2: 1 KK log at 999 views -> below both KK daily targets.
        var kkLog = host.Log(new DateTime(2026, 9, 2, 10, 0, 0), host.KkId);
        host.Db.ContentLogs.Add(kkLog);
        host.Db.ContentMetrics.Add(host.Metric(kkLog.Id, 999, new DateTime(2026, 9, 3, 0, 0, 0)));
        // Sep 2: 2 Auto GMV logs - included in TOTAL when the toggle is on (existing rule).
        for (var i = 0; i < 2; i++)
        {
            var log = host.Log(new DateTime(2026, 9, 2, 11, 0, 0), host.AutoGmvId);
            host.Db.ContentLogs.Add(log);
            host.Db.ContentMetrics.Add(host.Metric(log.Id, 500, new DateTime(2026, 9, 3, 1, i, 0)));
        }
        host.Save();

        var page = await host.DailySummaryService.BuildAsync(
            Filter(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7)), CancellationToken.None);

        var sep1 = page.Data.Rows.Single(r => r.Date == new DateTime(2026, 9, 1));
        Assert.Equal("above", sep1.NonKkContentStatus); // 3 >= 3 (NON_KK's own upload target)
        Assert.Equal("above", sep1.NonKkViewsStatus);   // 45,003 >= 0 (NON_KK views target = 0 for this seed)
        Assert.Equal(18m, sep1.TotalContentTarget);      // NON_KK 3 + KK 15, per-row combined daily target
        Assert.Equal(15_000m, sep1.TotalViewsTarget);    // NON_KK 0 + KK 15,000

        var sep2 = page.Data.Rows.Single(r => r.Date == new DateTime(2026, 9, 2));
        Assert.Equal("below", sep2.KkContentStatus);    // 1 < 15 (KK's own upload target)
        Assert.Equal("below", sep2.KkViewsStatus);      // 999 < 15,000 (KK's own views target)
        // TOTAL includes Auto GMV in the ACTUAL (1+2 = 3) but the target stays the combined
        // NON_KK + KK daily target (3 + 15 = 18) -> 3 < 18 = below (spec sections 16/17).
        Assert.Equal("below", sep2.TotalContentStatus);
        Assert.Equal(18m, sep2.TotalContentTarget);
    }

    [Fact]
    public async Task BuildAsync_PeriodAchievements_PerTypeAcrossEffectiveDateChange()
    {
        var host = new TestHost();
        // NON_KK: 10/week from Sep 1 (trimmed to Sep 14), 35/week from Sep 15.
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 10, views: 0,
            from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 14)));
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 35, views: 0,
            from: new DateOnly(2026, 9, 15)));
        // KK: separate views-only version so the per-type rows stay independent.
        host.Db.Targets.Add(host.WeeklyTarget(host.KkId, upload: 0, views: 7_000,
            from: new DateOnly(2026, 9, 1)));
        host.Save();

        var page = await host.DailySummaryService.BuildAsync(
            Filter(new DateTime(2026, 9, 14), new DateTime(2026, 9, 16)), CancellationToken.None);

        // Sep 14: 10/7 = 1.4286; Sep 15-16: 35/7 = 5 each.
        var rows = page.Data.Rows.OrderBy(r => r.Date).ToList();
        Assert.Equal(1.4286m, rows[0].TotalContentTarget); // NON_KK 1.4286 + KK 0
        Assert.Equal(5m, rows[1].TotalContentTarget);
        Assert.Equal(5m, rows[2].TotalContentTarget);

        // Pencapaian vs Target (NON_KK): (10 + 35 + 35)/7 = 80/7 = 11.4286 - per-date, not first/last x days.
        var contentAchievement = page.TargetAchievements.Single(a => a.Code == NonKk && a.MetricName == "Jumlah Konten");
        Assert.Equal(11.4286m, contentAchievement.TargetPeriodValue);
        Assert.Equal(0, contentAchievement.ActualValue); // no content seeded
        Assert.Equal(-11.4286m, contentAchievement.VarianceValue);
        Assert.Equal("not achieved", contentAchievement.Status);

        // Pencapaian vs Target (KK): (7,000*3)/7 = 3,000 views - from the KK versions only.
        var viewsAchievement = page.TargetAchievements.Single(a => a.Code == Kk && a.MetricName == "Total Views");
        Assert.Equal(3_000m, viewsAchievement.TargetPeriodValue);
    }

    [Fact]
    public async Task BuildAsync_WithoutAnyTarget_AllStatusesUseZeroTargets()
    {
        var host = new TestHost();
        // No Target rows at all: zeros convention (same as Menu Targets missing target).
        var log = host.Log(new DateTime(2026, 9, 1, 10, 0, 0), host.NonKkId);
        host.Db.ContentLogs.Add(log);
        host.Db.ContentMetrics.Add(host.Metric(log.Id, 100, new DateTime(2026, 9, 2, 0, 0, 0)));
        host.Save();

        var page = await host.DailySummaryService.BuildAsync(
            Filter(new DateTime(2026, 9, 1), new DateTime(2026, 9, 1)), CancellationToken.None);

        var row = page.Data.Rows.Single();
        Assert.Equal(0m, row.TotalContentTarget);
        Assert.Equal(0m, row.TotalViewsTarget);
        Assert.Equal("above", row.TotalContentStatus);  // actual >= 0
        Assert.Equal("above", row.NonKkViewsStatus);
        // Period target = 0 -> variance equals the actual value.
        var contentAchievement = page.TargetAchievements.Single(a => a.Code == NonKk && a.MetricName == "Jumlah Konten");
        Assert.Equal(0m, contentAchievement.TargetPeriodValue);
        Assert.Equal(1, contentAchievement.ActualValue);
    }

    // ============================================================
    // Ringkasan + Composition stability (target integration must not
    // change actual aggregation).
    // ============================================================

    [Fact]
    public async Task BuildAsync_SummaryActuals_And_Composition_UnchangedByTargetIntegration()
    {
        var host = new TestHost();
        // Targets present (integration active) - actuals must still aggregate identically.
        host.Db.Targets.Add(host.WeeklyTarget(host.NonKkId, upload: 13, views: 26_000, from: new DateOnly(2026, 9, 1)));
        host.Db.Targets.Add(host.WeeklyTarget(host.KkId, upload: 7, views: 4_500, from: new DateOnly(2026, 9, 1)));
        host.Save();

        // Seed the UAT-shaped actuals over Sep 1-30: NON_KK 12 logs / 4,635 views,
        // KK 13 / 1,444, Auto GMV 8 / 745. Latest-metric views per log are spread so
        // the sums hit the exact totals.
        void SeedType(int typeId, int logCount, long totalViews, int startHour)
        {
            for (var i = 0; i < logCount; i++)
            {
                var day = 1 + (i * 29 / Math.Max(1, logCount)); // spread across the month
                var log = host.Log(new DateTime(2026, 9, day, startHour, 0, 0), typeId);
                host.Db.ContentLogs.Add(log);
                host.Db.ContentMetrics.Add(host.Metric(log.Id, totalViews / logCount, new DateTime(2026, 9, day, 23, 0, 0)));
            }
        }
        SeedType(host.NonKkId, 12, 4_635, 8);
        SeedType(host.KkId, 13, 1_444, 9);
        SeedType(host.AutoGmvId, 8, 745, 10);
        host.Save();

        var page = await host.DailySummaryService.BuildAsync(
            Filter(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)), CancellationToken.None);

        // RINGKASAN: totals + averages over 30 calendar days (zero days stay in the denominator).
        var nonKk = page.TypeSummaries.Single(x => x.Code == NonKk);
        Assert.Equal(12, nonKk.TotalCount);
        Assert.Equal(0.4m, nonKk.AvgContentPerDay);      // 12/30
        var kk = page.TypeSummaries.Single(x => x.Code == Kk);
        Assert.Equal(13, kk.TotalCount);
        Assert.Equal(0.43m, kk.AvgContentPerDay);        // 13/30 = 0.4333 -> 0.43
        var autoGmv = page.TypeSummaries.Single(x => x.Code == "AUTO_GMV_LIVE");
        Assert.Equal(8, autoGmv.TotalCount);
        Assert.Equal(0.27m, autoGmv.AvgContentPerDay);   // 8/30 = 0.2667 (view displays 0,3 via "0.#")

        // COMPOSITION: 12/33, 13/33, 8/33 with independent rounding -> 36/39/24 (sum 99 is expected).
        Assert.Collection(page.Composition,
            c => Assert.Equal(36.36m, c.Percentage),
            c => Assert.Equal(39.39m, c.Percentage),
            c => Assert.Equal(24.24m, c.Percentage));

        // Pencapaian vs Target stays per type (NON_KK upload 13*30/7 = 55.7143) while
        // actuals above are untouched by the target integration.
        var contentAchievement = page.TargetAchievements.Single(a => a.Code == NonKk && a.MetricName == "Jumlah Konten");
        Assert.Equal(55.7143m, contentAchievement.TargetPeriodValue);
        Assert.Equal(12, contentAchievement.ActualValue);
    }
}
