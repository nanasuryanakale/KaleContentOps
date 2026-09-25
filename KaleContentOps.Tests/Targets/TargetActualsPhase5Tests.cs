using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using KaleContentOps.Services.Targets;
using Xunit;

namespace KaleContentOps.Tests.Targets;

/// <summary>
/// Phase 5 unit tests (Target Actual / Selisih / Status) over the EF InMemory provider,
/// consistent with the existing project pattern (xunit, no new framework).
/// Conventions asserted here mirror DailySummaryService (the existing source of truth):
/// half-open VideoPostTime window, classified ContentTypeId filter, IsArchived != true,
/// latest ContentMetric per ContentLog (CapturedAt DESC, Id DESC), no N+1.
/// </summary>
public class TargetActualsPhase5Tests
{
    private const string NonKk = TargetService.NonKkCode; // "NON_KK"
    private const string Kk = TargetService.KkCode;       // "KK"
    private const string AutoGmv = "AUTO_GMV_LIVE";

    private static readonly DateOnly Today = new(2026, 9, 24); // a Thursday

    private class TestHost
    {
        public string DatabaseName { get; } = Guid.NewGuid().ToString();
        public AppDbContext Db { get; }
        public TargetService Service { get; }
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
                new ContentType { Code = AutoGmv, Name = "Auto GMV Live" });
            Db.SaveChanges();

            NonKkId = Db.ContentTypes.Single(c => c.Code == NonKk).Id;
            KkId = Db.ContentTypes.Single(c => c.Code == Kk).Id;
            AutoGmvId = Db.ContentTypes.Single(c => c.Code == AutoGmv).Id;

            Service = new TargetService(Db, new StubShopTimeZone(Today));
        }

        public ContentLog Log(DateTime? postTime, int? contentTypeId = null, bool? archived = null) =>
            new()
            {
                VideoId = $"vid-{Guid.NewGuid():N}",
                VideoPostTime = postTime,
                ContentTypeId = contentTypeId,
                IsArchived = archived,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

        public ContentMetric Metric(long contentLogId, long? views, DateTime capturedAt) =>
            new()
            {
                ContentLogId = contentLogId,
                Views = views,
                CapturedAt = capturedAt
            };

        public void Save() => Db.SaveChanges();
    }

    /// <summary>Injectable server clock (Asia/Jakarta), same pattern as TargetServiceTests.</summary>
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
    // Window: exactly 7 calendar days, inclusive bounds
    // ============================================================

    [Fact]
    public async Task ActualsSummary_WindowIsExactlySevenCalendarDays_Inclusive()
    {
        var host = new TestHost();
        // Today = Thu 24 Sep 2026 -> rolling window Fri 18 Sep .. Thu 24 Sep (D-6..D0).
        host.Db.ContentLogs.AddRange(
            host.Log(new DateTime(2026, 9, 15, 12, 0, 0), host.NonKkId),   // D-9, outside
            host.Log(new DateTime(2026, 9, 18, 0, 0, 0), host.NonKkId),    // first day 00:00, inside
            host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId),   // D-4, inside
            host.Log(new DateTime(2026, 9, 24, 23, 59, 59), host.NonKkId), // today 23:59:59, inside
            host.Log(new DateTime(2026, 9, 25, 0, 0, 0), host.NonKkId));   // tomorrow, outside
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 9, 18), summary.StartDate);
        Assert.Equal(Today, summary.EndDate);
        Assert.Equal(7, summary.Days);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(3, nonKk.ActualUpload); // 18 + 20 + 24 Sep; 15 and 25 Sep excluded
    }

    [Fact]
    public async Task ActualsSummary_TodayLateEvening_Included()
    {
        var host = new TestHost();
        host.Db.ContentLogs.Add(host.Log(new DateTime(2026, 9, 24, 23, 59, 59), host.NonKkId));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(1, nonKk.ActualUpload);
    }

    [Fact]
    public async Task ActualsSummary_NextDayMidnight_Excluded()
    {
        var host = new TestHost();
        // 00:00 the next calendar day belongs to that next day, not to today.
        host.Db.ContentLogs.Add(host.Log(new DateTime(2026, 9, 25, 0, 0, 0), host.NonKkId));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(0, nonKk.ActualUpload);
    }

    [Fact]
    public async Task ActualsSummary_MondayToSundayWindow_IsNotUsed()
    {
        var host = new TestHost();
        // 24 Sep 2026 is a Thursday. A Mon-Sun window (21-27 Sep) would wrongly include
        // 26 Sep and exclude 18 Sep. Only the rolling window counts must appear.
        host.Db.ContentLogs.AddRange(
            host.Log(new DateTime(2026, 9, 18, 12, 0, 0), host.NonKkId),  // rolling first day
            host.Log(new DateTime(2026, 9, 26, 12, 0, 0), host.NonKkId)); // Sat, inside Mon-Sun only
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(1, nonKk.ActualUpload); // only 18 Sep
    }

    // ============================================================
    // Actual Upload: classification, archived, single count
    // ============================================================

    [Fact]
    public async Task ActualsSummary_UnclassifiedContentLog_IsNotCounted()
    {
        var host = new TestHost();
        // ContentTypeId == null must not leak into any bucket (existing rule:
        // DailySummaryService filters typeIds.Contains(ContentTypeId.Value)).
        host.Db.ContentLogs.AddRange(
            host.Log(new DateTime(2026, 9, 20, 12, 0, 0), contentTypeId: null),
            host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        var kk = summary.Items.Single(x => x.ContentTypeCode == Kk);
        Assert.Equal(1, nonKk.ActualUpload);
        Assert.Equal(0, kk.ActualUpload);
    }

    [Fact]
    public async Task ActualsSummary_ArchivedExcluded()
    {
        var host = new TestHost();
        host.Db.ContentLogs.AddRange(
            host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId, archived: true),
            host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId, archived: false));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(1, nonKk.ActualUpload);
    }

    [Fact]
    public async Task ActualsSummary_OneContentLog_CountedOnce_DespiteMultipleMetrics()
    {
        var host = new TestHost();
        var log = host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId);
        host.Db.ContentLogs.Add(log);
        host.Save();
        host.Db.ContentMetrics.AddRange(
            host.Metric(log.Id, 100, new DateTime(2026, 9, 20, 13, 0, 0)),
            host.Metric(log.Id, 150, new DateTime(2026, 9, 21, 13, 0, 0)),
            host.Metric(log.Id, 180, new DateTime(2026, 9, 22, 13, 0, 0)));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(1, nonKk.ActualUpload); // one log = one count; metrics never add uploads
        Assert.Equal(180, nonKk.ActualViews); // latest metric only
    }

    [Fact]
    public async Task ActualsSummary_UploadDoesNotDependOnMetrics()
    {
        var host = new TestHost();
        var withMetric = host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId);
        var withoutMetric = host.Log(new DateTime(2026, 9, 21, 12, 0, 0), host.NonKkId);
        host.Db.ContentLogs.AddRange(withMetric, withoutMetric);
        host.Save();
        host.Db.ContentMetrics.Add(host.Metric(withMetric.Id, 500, new DateTime(2026, 9, 20, 13, 0, 0)));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(2, nonKk.ActualUpload); // the metric-less log still counts as upload
        Assert.Equal(500, nonKk.ActualViews); // only the metric-bearing log contributes views
    }

    [Fact]
    public async Task ActualsSummary_AutoGmvLive_AbsentFromTargetRows_LogsNeverLeak()
    {
        var host = new TestHost();
        host.Db.ContentLogs.Add(host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.AutoGmvId));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        // AUTO_GMV_LIVE never appears as a target row (existing rule), and its logs
        // never leak into NON_KK/KK buckets.
        Assert.DoesNotContain(summary.Items, x => x.ContentTypeCode == AutoGmv);
        Assert.All(summary.Items, item => Assert.Equal(0, item.ActualUpload));
    }

    // ============================================================
    // Actual Views: latest metric, no double counting
    // ============================================================

    [Fact]
    public async Task ActualsSummary_LatestMetricByCapturedAt_IsUsed()
    {
        var host = new TestHost();
        var log = host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId);
        host.Db.ContentLogs.Add(log);
        host.Save();
        // Insert OLDER snapshot AFTER the newer one: Id order must not beat CapturedAt order.
        host.Db.ContentMetrics.AddRange(
            host.Metric(log.Id, 180, new DateTime(2026, 9, 22, 13, 0, 0)),  // latest CapturedAt
            host.Metric(log.Id, 100, new DateTime(2026, 9, 20, 13, 0, 0))); // older
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(180, nonKk.ActualViews);
    }

    [Fact]
    public async Task ActualsSummary_MultipleSnapshots_NeverSummed()
    {
        var host = new TestHost();
        var logA = host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId);
        var logB = host.Log(new DateTime(2026, 9, 21, 12, 0, 0), host.KkId);
        host.Db.ContentLogs.AddRange(logA, logB);
        host.Save();
        host.Db.ContentMetrics.AddRange(
            host.Metric(logA.Id, 100, new DateTime(2026, 9, 20, 13, 0, 0)),
            host.Metric(logA.Id, 150, new DateTime(2026, 9, 21, 13, 0, 0)),
            host.Metric(logA.Id, 180, new DateTime(2026, 9, 22, 13, 0, 0)),
            host.Metric(logB.Id, 10, new DateTime(2026, 9, 21, 13, 0, 0)),
            host.Metric(logB.Id, 20, new DateTime(2026, 9, 22, 13, 0, 0)));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        var kk = summary.Items.Single(x => x.ContentTypeCode == Kk);
        Assert.Equal(180, nonKk.ActualViews); // 100+150+180 would be 430
        Assert.Equal(20, kk.ActualViews);     // 10+20 would be 30
    }

    [Fact]
    public async Task ActualsSummary_CapturedAtTie_HighestIdWins()
    {
        var host = new TestHost();
        var log = host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId);
        host.Db.ContentLogs.Add(log);
        host.Save();
        host.Db.ContentMetrics.AddRange(
            host.Metric(log.Id, 100, new DateTime(2026, 9, 21, 13, 0, 0)),
            host.Metric(log.Id, 180, new DateTime(2026, 9, 21, 13, 0, 0))); // same CapturedAt
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(180, nonKk.ActualViews); // highest Id wins (existing DailySummary tiebreak)
    }

    [Fact]
    public async Task ActualsSummary_MissingMetric_ContributesZeroViewsButStillUpload()
    {
        var host = new TestHost();
        var withMetric = host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId);
        var withoutMetric = host.Log(new DateTime(2026, 9, 21, 12, 0, 0), host.NonKkId);
        host.Db.ContentLogs.AddRange(withMetric, withoutMetric);
        host.Save();
        host.Db.ContentMetrics.Add(host.Metric(withMetric.Id, 250, new DateTime(2026, 9, 20, 13, 0, 0)));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(2, nonKk.ActualUpload);
        Assert.Equal(250, nonKk.ActualViews); // missing metric contributes 0, no failure
    }

    [Fact]
    public async Task ActualsSummary_NullViewsInLatestMetric_ContributesZero()
    {
        var host = new TestHost();
        var log = host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId);
        host.Db.ContentLogs.Add(log);
        host.Save();
        host.Db.ContentMetrics.AddRange(
            host.Metric(log.Id, 300, new DateTime(2026, 9, 20, 13, 0, 0)),
            host.Metric(log.Id, null, new DateTime(2026, 9, 21, 13, 0, 0))); // latest is NULL
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(0, nonKk.ActualViews); // existing convention: latest null Views -> 0
    }

    // ============================================================
    // Target: date-resolved, scheduled excluded
    // ============================================================

    [Fact]
    public async Task ActualsSummary_TargetEffectiveOnEndDate_IsUsed()
    {
        var host = new TestHost();
        host.Db.Targets.Add(new Target
        {
            ContentTypeId = host.NonKkId,
            TargetUpload = 15,
            TargetViews = 30_000,
            EffectiveFrom = new DateOnly(2026, 9, 1)
        });
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(15, nonKk.TargetUpload);
        Assert.Equal(30_000, nonKk.TargetViews);
        Assert.Equal(new DateOnly(2026, 9, 1), nonKk.TargetEffectiveFrom);
    }

    [Fact]
    public async Task ActualsSummary_FutureTargetNotUsedBeforeEffectiveDate()
    {
        var host = new TestHost();
        host.Db.Targets.AddRange(
            new Target { ContentTypeId = host.NonKkId, TargetUpload = 15, TargetViews = 30_000, EffectiveFrom = new DateOnly(2026, 9, 1) },
            new Target { ContentTypeId = host.NonKkId, TargetUpload = 20, TargetViews = 40_000, EffectiveFrom = new DateOnly(2026, 10, 1) });
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        // On 24 Sep the current target is 15/30.000, NOT the scheduled 20/40.000.
        Assert.Equal(15, nonKk.TargetUpload);
        Assert.Equal(30_000, nonKk.TargetViews);
    }

    [Fact]
    public async Task ActualsSummary_MissingTarget_ZerosPlaceholder_ActualsStillShown()
    {
        var host = new TestHost();
        host.Db.ContentLogs.Add(host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(0, nonKk.TargetUpload);
        Assert.Equal(0, nonKk.TargetViews);
        Assert.Null(nonKk.TargetEffectiveFrom);
        Assert.Equal(1, nonKk.ActualUpload);
        Assert.Equal(1, nonKk.SelisihUpload); // 1 - 0
        // Literal rule: 1 >= 0 uploads AND 0 >= 0 views -> Tercapai (selisih 0 = met,
        // same convention as the existing per-metric status in the Phase 3 UI).
        Assert.Equal("Tercapai", nonKk.Status);
    }

    // ============================================================
    // Selisih: positive / negative / zero
    // ============================================================

    [Fact]
    public async Task ActualsSummary_Selisih_Positive()
    {
        var host = new TestHost();
        host.Db.Targets.Add(new Target { ContentTypeId = host.NonKkId, TargetUpload = 1, TargetViews = 1_000, EffectiveFrom = new DateOnly(2026, 9, 1) });
        host.Db.ContentLogs.AddRange(
            host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId),
            host.Log(new DateTime(2026, 9, 21, 12, 0, 0), host.NonKkId));
        host.Save();
        host.Db.ContentMetrics.Add(host.Metric(host.Db.ContentLogs.Local.First().Id, 1_500, new DateTime(2026, 9, 20, 13, 0, 0)));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(1, nonKk.SelisihUpload);   // 2 - 1
        Assert.Equal(500, nonKk.SelisihViews);  // 1500 - 1000
    }

    [Fact]
    public async Task ActualsSummary_Selisih_Negative()
    {
        var host = new TestHost();
        host.Db.Targets.Add(new Target { ContentTypeId = host.NonKkId, TargetUpload = 15, TargetViews = 30_000, EffectiveFrom = new DateOnly(2026, 9, 1) });
        host.Db.ContentLogs.Add(host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId));
        host.Save();
        host.Db.ContentMetrics.Add(host.Metric(host.Db.ContentLogs.Local.Single().Id, 12_000, new DateTime(2026, 9, 20, 13, 0, 0)));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(-14, nonKk.SelisihUpload);
        Assert.Equal(-18_000, nonKk.SelisihViews);
    }

    [Fact]
    public async Task ActualsSummary_Selisih_Zero_IsAchieved()
    {
        var host = new TestHost();
        host.Db.Targets.Add(new Target { ContentTypeId = host.NonKkId, TargetUpload = 2, TargetViews = 300, EffectiveFrom = new DateOnly(2026, 9, 1) });
        host.Db.ContentLogs.AddRange(
            host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId),
            host.Log(new DateTime(2026, 9, 21, 12, 0, 0), host.NonKkId));
        host.Save();
        host.Db.ContentMetrics.Add(host.Metric(host.Db.ContentLogs.Local.First().Id, 300, new DateTime(2026, 9, 20, 13, 0, 0)));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(0, nonKk.SelisihUpload);
        Assert.Equal(0, nonKk.SelisihViews);
        Assert.Equal("Tercapai", nonKk.Status); // 0 selisih counts as met (existing convention)
    }

    // ============================================================
    // Status: combined rule
    // ============================================================

    [Fact]
    public void Status_BothMetricsAchieved_ReturnsTercapai()
    {
        var status = TargetActualStatus.Resolve(actualUpload: 15, targetUpload: 15, actualViews: 30_000, targetViews: 30_000);
        Assert.Equal(TargetActualStatus.Achieved, status);
    }

    [Fact]
    public void Status_UploadAchieved_ViewsNot_ReturnsBelumTercapai()
    {
        var status = TargetActualStatus.Resolve(actualUpload: 12, targetUpload: 15, actualViews: 35_000, targetViews: 30_000);
        Assert.Equal(TargetActualStatus.NotAchieved, status);
    }

    [Fact]
    public void Status_ViewsAchieved_UploadNot_ReturnsBelumTercapai()
    {
        var status = TargetActualStatus.Resolve(actualUpload: 16, targetUpload: 15, actualViews: 29_999, targetViews: 30_000);
        Assert.Equal(TargetActualStatus.NotAchieved, status);
    }

    [Fact]
    public void Status_NeitherAchieved_ReturnsBelumTercapai()
    {
        var status = TargetActualStatus.Resolve(actualUpload: 1, targetUpload: 15, actualViews: 1, targetViews: 30_000);
        Assert.Equal(TargetActualStatus.NotAchieved, status);
    }

    // ============================================================
    // Edge cases
    // ============================================================

    [Fact]
    public void Status_TargetZero_ActualZero_Tercapai()
    {
        var status = TargetActualStatus.Resolve(actualUpload: 0, targetUpload: 0, actualViews: 0, targetViews: 0);
        Assert.Equal(TargetActualStatus.Achieved, status);
    }

    [Fact]
    public async Task ActualsSummary_EmptyDataset_ReturnsZeros()
    {
        var host = new TestHost();
        // No content logs, no metrics, no targets at all.

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        Assert.Equal(2, summary.Items.Count);
        Assert.All(summary.Items, item =>
        {
            Assert.Equal(0, item.ActualUpload);
            Assert.Equal(0, item.ActualViews);
            Assert.Equal(0, item.TargetUpload);
            Assert.Equal(0, item.TargetViews);
            // Literal rule: 0 actual >= 0 target on BOTH metrics -> Tercapai (selisih 0 = met,
            // same convention as the existing per-metric status in the Phase 3 UI).
            Assert.Equal("Tercapai", item.Status);
        });
    }

    [Fact]
    public async Task ActualsSummary_NullVideoPostTime_Excluded()
    {
        var host = new TestHost();
        host.Db.ContentLogs.AddRange(
            host.Log(null, host.NonKkId),
            host.Log(new DateTime(2026, 9, 20, 12, 0, 0), host.NonKkId));
        host.Save();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        var nonKk = summary.Items.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(1, nonKk.ActualUpload); // null post time never counted (existing convention)
    }

    [Fact]
    public async Task ActualsSummary_OrdersNonKkFirst()
    {
        var host = new TestHost();

        var summary = await host.Service.GetActualsSummaryAsync(Today, 7, CancellationToken.None);

        Assert.Equal(NonKk, summary.Items[0].ContentTypeCode);
        Assert.Equal(Kk, summary.Items[1].ContentTypeCode);
    }

    // ============================================================
    // GetActualAsync boundary regression (aligned with the summary pipeline)
    // ============================================================

    [Fact]
    public async Task GetActual_SameBoundaryConventions_AsSummary()
    {
        var host = new TestHost();
        host.Db.ContentLogs.AddRange(
            host.Log(new DateTime(2026, 9, 24, 23, 59, 59), host.NonKkId), // today 23:59:59 inside
            host.Log(new DateTime(2026, 9, 25, 0, 0, 0), host.NonKkId));   // tomorrow outside
        host.Save();

        var actual = await host.Service.GetActualAsync(host.NonKkId, Today, 7, CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal(1, actual!.ActualUpload);
    }

    [Fact]
    public async Task GetActual_UnknownContentType_ReturnsNull()
    {
        var host = new TestHost();

        var actual = await host.Service.GetActualAsync(9999, Today, 7, CancellationToken.None);

        Assert.Null(actual);
    }
}
