using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services.WinningContent;
using Xunit;

namespace KaleContentOps.Tests.WinningContent;

/// <summary>
/// Phase 2A backend data foundation tests for Winning Content.
/// Covers: content type grouping, latest-metric selection, temporary ER (incl. Views = 0
/// and NULL metrics), baseline 30-day boundary + average-of-per-video-ER, AUTO_GMV_LIVE
/// inclusion, per-content-type median input, and content-count composition.
/// Uses the EF Core InMemory provider (same as the other integration-style suites).
/// </summary>
public class WinningContentServiceTests
{
    private const string NonKk = "NON_KK";
    private const string Kk = "KK";
    private const string AutoGmv = "AUTO_GMV_LIVE";

    private sealed class Host : IDisposable
    {
        public AppDbContext Db { get; }
        public WinningContentService Service { get; }
        public int NonKkId { get; }
        public int KkId { get; }
        public int AutoGmvId { get; }

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

            NonKkId = Db.ContentTypes.Single(c => c.Code == NonKk).Id;
            KkId = Db.ContentTypes.Single(c => c.Code == Kk).Id;
            AutoGmvId = Db.ContentTypes.Single(c => c.Code == AutoGmv).Id;

            Service = new WinningContentService(Db);
        }

        /// <summary>Add a ContentLog; contentTypeCode null means unclassified.</summary>
        public ContentLog Log(DateTime postTime, string? contentTypeCode, bool archived = false, string? title = null)
        {
            var log = new ContentLog
            {
                VideoId = Guid.NewGuid().ToString("N"),
                Title = title,
                VideoPostTime = postTime,
                ContentTypeId = contentTypeCode == null
                    ? null
                    : Db.ContentTypes.Single(c => c.Code == contentTypeCode).Id,
                IsArchived = archived
            };
            Db.ContentLogs.Add(log);
            Db.SaveChanges();
            return log;
        }

        public ContentMetric Metric(ContentLog log, long? views, long? likes, long? comments, long? shares, DateTime? capturedAt = null)
        {
            var metric = new ContentMetric
            {
                ContentLogId = log.Id,
                Views = views,
                Likes = likes,
                Comments = comments,
                Shares = shares,
                CapturedAt = capturedAt ?? DateTime.UtcNow
            };
            Db.ContentMetrics.Add(metric);
            Db.SaveChanges();
            return metric;
        }

        public void Dispose() => Db.Dispose();
    }

    private static WinningContentFilter Period(int startYear, int startMonth, int startDay, int days) => new()
    {
        // Inclusive range of `days` calendar days.
        StartDate = new DateTime(startYear, startMonth, startDay),
        EndDate = new DateTime(startYear, startMonth, startDay).AddDays(days - 1)
    };

    // ------------------------------------------------------------------ ER calculator

    [Fact]
    public void ComputeEngagementRate_NormalCase_ReturnsExpectedValue()
    {
        // (13 + 2 + 1) / 263
        var er = WinningContentCalculator.ComputeEngagementRate(13, 2, 1, 263);
        Assert.Equal((13m + 2m + 1m) / 263m, er);
    }

    [Fact]
    public void ComputeEngagementRate_ViewsZero_ReturnsNull_NotDivideByZero()
    {
        Assert.Null(WinningContentCalculator.ComputeEngagementRate(10, 2, 1, 0));
    }

    [Fact]
    public void ComputeEngagementRate_NullMetrics_ReturnsNull_NeverFabricates()
    {
        Assert.Null(WinningContentCalculator.ComputeEngagementRate(null, 2, 1, 100));
        Assert.Null(WinningContentCalculator.ComputeEngagementRate(10, null, 1, 100));
        Assert.Null(WinningContentCalculator.ComputeEngagementRate(10, 2, null, 100));
        Assert.Null(WinningContentCalculator.ComputeEngagementRate(10, 2, 1, null));
    }

    // ------------------------------------------------------------------ 1) grouping + 8) Auto GMV Live included

    [Fact]
    public async Task BuildAsync_Groups_By_ContentType_And_Includes_AutoGmvLive()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        var nonKk = host.Log(day, NonKk);
        var kk = host.Log(day, Kk);
        var autoGmv = host.Log(day, AutoGmv); // archived: MUST still be included (locked rule 2)
        host.Db.Entry(autoGmv).Property(l => l.IsArchived).CurrentValue = true;

        host.Metric(nonKk, 100, 5, 1, 0);
        host.Metric(kk, 200, 10, 2, 1);
        host.Metric(autoGmv, 300, 15, 3, 2);

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        Assert.Equal(3, data.Items.Count);
        Assert.Contains(data.Items, x => x.ContentTypeCode == NonKk && x.ContentLogId == nonKk.Id);
        Assert.Contains(data.Items, x => x.ContentTypeCode == Kk && x.ContentLogId == kk.Id);
        Assert.Contains(data.Items, x => x.ContentTypeCode == AutoGmv && x.ContentLogId == autoGmv.Id);
    }

    // ------------------------------------------------------------------ 2) latest metric selection

    [Fact]
    public async Task BuildAsync_Uses_Latest_Metric_By_CapturedAt_And_Preserves_Nulls()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);
        var log = host.Log(day, NonKk);

        // Older snapshot has engagement; newest snapshot has NULL Likes/Comments/Shares.
        host.Metric(log, 100, 5, 1, 0, capturedAt: new DateTime(2026, 9, 11, 1, 0, 0));
        host.Metric(log, 150, null, null, null, capturedAt: new DateTime(2026, 9, 11, 2, 0, 0));

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));
        var item = Assert.Single(data.Items);

        Assert.Equal(150, item.Views);          // latest snapshot wins
        Assert.Null(item.Likes);                // newest values are NULL -> stay NULL
        Assert.Null(item.Comments);
        Assert.Null(item.Shares);
        Assert.Null(item.EngagementRate);       // no fabricated numerator
        Assert.Equal(new DateTime(2026, 9, 11, 2, 0, 0), item.LatestMetricCapturedAt);
    }

    [Fact]
    public async Task BuildAsync_LogWithoutMetric_Produces_Nulls_And_NullCapturedAt()
    {
        using var host = new Host();
        host.Log(new DateTime(2026, 9, 10, 12, 0, 0), Kk);

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));
        var item = Assert.Single(data.Items);

        Assert.Null(item.Views);
        Assert.Null(item.Likes);
        Assert.Null(item.Comments);
        Assert.Null(item.Shares);
        Assert.Null(item.EngagementRate);
        Assert.Null(item.LatestMetricCapturedAt);
    }

    [Fact]
    public async Task BuildAsync_LatestMetricTieBreaks_By_Id()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);
        var log = host.Log(day, NonKk);

        var first = host.Metric(log, 100, 5, 1, 0, capturedAt: new DateTime(2026, 9, 11, 1, 0, 0));
        var second = host.Metric(log, 120, 6, 1, 0, capturedAt: new DateTime(2026, 9, 11, 1, 0, 0)); // same CapturedAt

        Assert.True(second.Id > first.Id);
        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));
        var item = Assert.Single(data.Items);
        Assert.Equal(120, item.Views);
        Assert.Equal(6, item.Likes);
    }

    // ------------------------------------------------------------------ 3) + 4) ER on period items

    [Fact]
    public async Task BuildAsync_Computes_TemporaryEngagementRate_ForPeriodItems()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        var withEr = host.Log(day, NonKk);
        host.Metric(withEr, 200, 10, 2, 4);     // ER = 16/200 = 0.08
        var zeroViews = host.Log(day, Kk);
        host.Metric(zeroViews, 0, 7, 0, 0);     // Views = 0 -> ER NULL, Views stays 0
        var nullMetrics = host.Log(day, AutoGmv);
        host.Metric(nullMetrics, null, null, null, null); // all NULL -> ER NULL

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        var erItem = data.Items.Single(x => x.ContentLogId == withEr.Id);
        Assert.Equal(0.08m, erItem.EngagementRate);
        Assert.Equal(200, erItem.Views);

        var zeroItem = data.Items.Single(x => x.ContentLogId == zeroViews.Id);
        Assert.Equal(0, zeroItem.Views);        // zero preserved as zero
        Assert.Null(zeroItem.EngagementRate);   // safe NULL, not divide-by-zero

        var nullItem = data.Items.Single(x => x.ContentLogId == nullMetrics.Id);
        Assert.Null(nullItem.Views);
        Assert.Null(nullItem.EngagementRate);
    }

    // ------------------------------------------------------------------ 6) baseline window + 7) average of per-video ER

    [Fact]
    public async Task BuildAsync_Baseline_Window_Is_30_CalendarDays_Before_PeriodStart()
    {
        using var host = new Host();
        // Baseline window for a 2026-09-10 period start = [2026-08-11, 2026-09-10).
        var inside1 = host.Log(new DateTime(2026, 8, 11, 0, 0, 0), NonKk);      // first baseline day, inside
        var inside2 = host.Log(new DateTime(2026, 9, 9, 23, 59, 59), NonKk);    // last baseline day, inside
        var tooEarly = host.Log(new DateTime(2026, 8, 10, 23, 59, 59), NonKk);  // one tick before window
        var atStart = host.Log(new DateTime(2026, 9, 10, 0, 0, 0), NonKk);      // period start, NOT baseline

        foreach (var log in new[] { inside1, inside2, tooEarly, atStart })
        {
            host.Metric(log, 100, 6, 0, 0);
        }

        var data = await host.Service.BuildAsync(Period(2026, 9, 10, 1));

        Assert.Equal(new DateTime(2026, 8, 11), data.BaselineStart);
        Assert.Equal(new DateTime(2026, 9, 10), data.BaselineEndExclusive);

        var baseline = data.Baselines.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(2, baseline.VideoCount);   // inside1 + inside2 only
        Assert.Equal(0.06m, baseline.AverageEngagementRate);
    }

    [Fact]
    public async Task BuildAsync_Baseline_Is_Average_Of_PerVideo_ER_Not_Aggregate()
    {
        using var host = new Host();

        var v1 = host.Log(new DateTime(2026, 8, 15, 12, 0, 0), NonKk);
        host.Metric(v1, 1000, 10, 0, 0);        // per-video ER = 10/1000 = 0.010

        var v2 = host.Log(new DateTime(2026, 8, 20, 12, 0, 0), NonKk);
        host.Metric(v2, 100, 2, 0, 0);          // per-video ER = 2/100 = 0.020

        var data = await host.Service.BuildAsync(Period(2026, 9, 10, 1));
        var baseline = data.Baselines.Single(x => x.ContentTypeCode == NonKk);

        // AVG(0.010, 0.020) = 0.015. Aggregate ER would be 12/1100 = 0.010909... - wrong.
        Assert.Equal(2, baseline.VideoCount);
        Assert.Equal(0.015m, baseline.AverageEngagementRate);
        Assert.NotEqual(12m / 1100m, baseline.AverageEngagementRate);
    }

    [Fact]
    public async Task BuildAsync_Baseline_Groups_By_ContentType_And_Skips_Unusable_ER_Videos()
    {
        using var host = new Host();

        var nonKkBase = host.Log(new DateTime(2026, 8, 15, 12, 0, 0), NonKk);
        host.Metric(nonKkBase, 100, 5, 0, 0);   // ER = 0.05

        var kkBase = host.Log(new DateTime(2026, 8, 16, 12, 0, 0), Kk);
        host.Metric(kkBase, 400, 4, 0, 0);      // ER = 0.01

        var zeroViewsBase = host.Log(new DateTime(2026, 8, 17, 12, 0, 0), NonKk);
        host.Metric(zeroViewsBase, 0, 9, 9, 9); // ER NULL (Views = 0) -> excluded from AVG

        var noMetricBase = host.Log(new DateTime(2026, 8, 18, 12, 0, 0), NonKk); // no metric -> excluded

        var data = await host.Service.BuildAsync(Period(2026, 9, 10, 1));

        var nonKkBaseline = data.Baselines.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(1, nonKkBaseline.VideoCount);
        Assert.Equal(0.05m, nonKkBaseline.AverageEngagementRate);

        var kkBaseline = data.Baselines.Single(x => x.ContentTypeCode == Kk);
        Assert.Equal(1, kkBaseline.VideoCount);
        Assert.Equal(0.01m, kkBaseline.AverageEngagementRate);
    }

    [Fact]
    public async Task BuildAsync_Baseline_Uses_Latest_Baseline_Metric()
    {
        using var host = new Host();
        var log = host.Log(new DateTime(2026, 8, 15, 12, 0, 0), NonKk);
        host.Metric(log, 100, 5, 0, 0, capturedAt: new DateTime(2026, 8, 20, 1, 0, 0));
        host.Metric(log, 200, 4, 0, 0, capturedAt: new DateTime(2026, 8, 21, 1, 0, 0)); // latest -> ER = 4/200 = 0.02

        var data = await host.Service.BuildAsync(Period(2026, 9, 10, 1));
        var baseline = data.Baselines.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(1, baseline.VideoCount);
        Assert.Equal(0.02m, baseline.AverageEngagementRate);
    }

    // ------------------------------------------------------------------ 9) median input grouped per content type

    [Fact]
    public async Task BuildAsync_MedianInput_Is_Grouped_Per_ContentType()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        var kk1 = host.Log(day, Kk);
        host.Metric(kk1, 100, 1, 0, 0);
        var kk2 = host.Log(day, Kk);
        host.Metric(kk2, 300, 1, 0, 0);
        var nonKk1 = host.Log(day, NonKk);
        host.Metric(nonKk1, 200, 1, 0, 0);
        var nonKkNoMetric = host.Log(day, NonKk);           // NULL Views stays in the input as NULL
        var autoGmv = host.Log(day, AutoGmv);
        host.Metric(autoGmv, 500, 1, 0, 0);

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        var kkInput = data.MedianInputs.Single(x => x.ContentTypeCode == Kk);
        Assert.Equal(new long?[] { 100, 300 }, kkInput.Views.OrderBy(v => v).ToArray());

        var nonKkInput = data.MedianInputs.Single(x => x.ContentTypeCode == NonKk);
        Assert.Equal(2, nonKkInput.Views.Count);
        Assert.Contains((long?)200, nonKkInput.Views);
        Assert.Contains((long?)null, nonKkInput.Views);     // NULL preserved, never coerced

        var autoGmvInput = data.MedianInputs.Single(x => x.ContentTypeCode == AutoGmv);
        Assert.Equal(new long?[] { 500 }, autoGmvInput.Views);

        // Groups are separate - no cross-type mixing (no AVG approximation here either).
        Assert.Equal(3, data.MedianInputs.Count);
    }

    // ------------------------------------------------------------------ 10) composition uses content count

    [Fact]
    public async Task BuildAsync_Composition_Uses_Content_Count_Including_AutoGmv()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        host.Log(day, NonKk);
        host.Log(day, NonKk);
        host.Log(day, Kk);
        var autoGmv = host.Log(day, AutoGmv);
        host.Db.Entry(autoGmv).Property(l => l.IsArchived).CurrentValue = true; // still counted

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        Assert.Equal(2, data.Composition.Single(x => x.ContentTypeCode == NonKk).Count);
        Assert.Equal(1, data.Composition.Single(x => x.ContentTypeCode == Kk).Count);
        Assert.Equal(1, data.Composition.Single(x => x.ContentTypeCode == AutoGmv).Count);

        // Denominator = NON_KK + KK + AUTO_GMV_LIVE = 4 (locked rule 8).
        var totalCount = data.Composition.Sum(x => x.Count);
        Assert.Equal(4, totalCount);
        Assert.Equal(data.Items.Count, totalCount);
    }

    // ------------------------------------------------------------------ period boundary sanity

    [Fact]
    public async Task BuildAsync_Period_Is_Inclusive_And_Excludes_Outside_Content()
    {
        using var host = new Host();

        var firstDay = host.Log(new DateTime(2026, 9, 1, 0, 0, 0), NonKk);      // inclusive start
        var lastDay = host.Log(new DateTime(2026, 9, 7, 23, 59, 59), NonKk);    // inclusive end
        var before = host.Log(new DateTime(2026, 8, 31, 23, 59, 59), NonKk);
        var after = host.Log(new DateTime(2026, 9, 8, 0, 0, 0), NonKk);

        foreach (var log in new[] { firstDay, lastDay, before, after })
        {
            host.Metric(log, 100, 5, 0, 0);
        }

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 7));

        Assert.Equal(2, data.Items.Count);
        Assert.Contains(data.Items, x => x.ContentLogId == firstDay.Id);
        Assert.Contains(data.Items, x => x.ContentLogId == lastDay.Id);
    }

    [Fact]
    public async Task BuildAsync_UnknownContentType_Logs_Are_Excluded()
    {
        using var host = new Host();
        var unclassified = host.Log(new DateTime(2026, 9, 10, 12, 0, 0), null);
        host.Metric(unclassified, 100, 5, 0, 0);

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));
        Assert.Empty(data.Items);
        Assert.All(data.Composition, c => Assert.Equal(0, c.Count));
    }
}
