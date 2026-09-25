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
/// Phase 2B tests: multiplier and leaderboard (Top-N by ER, per content type).
/// Locked rules under test: ER = (Likes+Comments+Shares)/Views with NULLIF(Views,0);
/// baseline = AVG of per-video ER over [PeriodStart-30d, PeriodStart) grouped by type;
/// Multiplier = ER / same-type baseline with NULL/0 baseline -> NULL multiplier;
/// ranking ONLY - NO minimum ER or multiplier threshold; NON_KK Top 4, KK Top 4,
/// AUTO_GMV_LIVE Top 2; tie-break ER DESC then ContentLogId DESC.
/// </summary>
public class WinningContentLeaderboardTests
{
    private const string NonKk = "NON_KK";
    private const string Kk = "KK";
    private const string AutoGmv = "AUTO_GMV_LIVE";

    /// <summary>Fixed period: [2026-09-10 .. 2026-09-16]; baseline = [2026-08-11, 2026-09-10).</summary>
    private static WinningContentFilter Filter() => new()
    {
        StartDate = new DateTime(2026, 9, 10),
        EndDate = new DateTime(2026, 9, 16)
    };

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

        public ContentLog Log(DateTime postTime, string typeCode, bool archived = false)
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
            Db.SaveChanges();
            return log;
        }

        /// <summary>Add a log whose latest metric yields exactly likes/views ER (views=1000 base).</summary>
        public ContentLog LogWithEr(DateTime postTime, string typeCode, long views, long likes, long comments = 0, long shares = 0, bool archived = false)
        {
            var log = Log(postTime, typeCode, archived);
            Db.ContentMetrics.Add(new ContentMetric
            {
                ContentLogId = log.Id,
                Views = views,
                Likes = likes,
                Comments = comments,
                Shares = shares,
                CapturedAt = DateTime.UtcNow
            });
            Db.SaveChanges();
            return log;
        }

        public void Dispose() => Db.Dispose();
    }

    private static DateTime BaselineDay(int day) => new(2026, 8, 11 + (day - 1), 12, 0, 0); // Aug 11 .. Sep 9
    private static DateTime PeriodDay(int day) => new(2026, 9, 10 + day - 1, 12, 0, 0);     // Sep 10 .. Sep 16

    // ------------------------------------------------------------------ multiplier calculator

    [Fact]
    public void ComputeMultiplier_SelectedEr3pct_Baseline1_5pct_Returns_2()
    {
        Assert.Equal(2m, WinningContentCalculator.ComputeMultiplier(0.030m, 0.015m));
    }

    [Fact]
    public void ComputeMultiplier_SelectedEr0_7pct_Baseline1pct_Returns_0_7()
    {
        // Sub-1.0x multiplier is a VALID VALUE - it must never be filtered or clamped.
        Assert.Equal(0.7m, WinningContentCalculator.ComputeMultiplier(0.007m, 0.010m));
    }

    [Fact]
    public void ComputeMultiplier_NullOrZeroBaseline_ReturnsNull_NeverFabricates()
    {
        Assert.Null(WinningContentCalculator.ComputeMultiplier(0.03m, null));   // no usable baseline
        Assert.Null(WinningContentCalculator.ComputeMultiplier(0.03m, 0m));     // zero baseline
        Assert.Null(WinningContentCalculator.ComputeMultiplier(null, 0.015m));  // no valid ER
    }

    [Fact]
    public void ComputeMultiplier_IsUnrounded()
    {
        // ER = 3/999, baseline = 1/1000 -> multiplier = 3.003003003... - must stay full precision.
        var expected = (3m / 999m) / (1m / 1000m);
        Assert.Equal(expected, WinningContentCalculator.ComputeMultiplier(3m / 999m, 1m / 1000m));
        Assert.NotEqual(3.00m, WinningContentCalculator.ComputeMultiplier(3m / 999m, 1m / 1000m));
    }

    // ------------------------------------------------------------------ baseline -> multiplier edge cases

    [Fact]
    public async Task NoUsableBaseline_Multiplier_Is_Null()
    {
        using var host = new Host();
        // Period item with valid ER; NO baseline videos at all.
        var item = host.LogWithEr(PeriodDay(1), NonKk, views: 1000, likes: 30);

        var data = await host.Service.BuildAsync(Filter());
        var baseline = data.Baselines.Single(b => b.ContentTypeCode == NonKk);
        Assert.Null(baseline.AverageEngagementRate);
        Assert.Equal(0, baseline.VideoCount);

        var entry = data.Items.Single(x => x.ContentLogId == item.Id);
        Assert.Equal(0.03m, entry.EngagementRate);
        Assert.Null(entry.BaselineEngagementRate);
        Assert.Null(entry.Multiplier);

        // Still ranked - ranking has no threshold; multiplier is information only.
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk);
        Assert.Equal(item.Id, Assert.Single(lb.Entries).ContentLogId);
    }

    [Fact]
    public async Task ZeroBaseline_Multiplier_Is_Null_And_Entry_Remains_Eligible()
    {
        using var host = new Host();
        // Baseline video with valid ER = 0 (0 interactions / 100 views) -> baseline ER = 0.
        host.LogWithEr(BaselineDay(1), NonKk, views: 100, likes: 0, comments: 0, shares: 0);
        var item = host.LogWithEr(PeriodDay(1), NonKk, views: 1000, likes: 30);

        var data = await host.Service.BuildAsync(Filter());
        var baseline = data.Baselines.Single(b => b.ContentTypeCode == NonKk);
        Assert.Equal(0m, baseline.AverageEngagementRate);

        var entry = data.Items.Single(x => x.ContentLogId == item.Id);
        Assert.Null(entry.Multiplier);          // no divide by zero, no fabricated 1x
        Assert.NotEqual(1m, entry.Multiplier);  // explicit: never fabricated as 1x

        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk);
        Assert.Equal(item.Id, Assert.Single(lb.Entries).ContentLogId); // ranking unaffected
    }

    [Fact]
    public async Task Multiplier_Uses_Same_Type_Baseline_Never_Another_Category()
    {
        using var host = new Host();
        // NON_KK baseline = 0.020, KK baseline = 0.010 (independent, per type).
        host.LogWithEr(BaselineDay(1), NonKk, views: 1000, likes: 20);
        host.LogWithEr(BaselineDay(1), Kk, views: 1000, likes: 10);

        // KK item ER = 0.030 -> multiplier 0.030/0.010 = 3.0 (NOT 0.030/0.020 = 1.5).
        var kkItem = host.LogWithEr(PeriodDay(1), Kk, views: 1000, likes: 30);

        var data = await host.Service.BuildAsync(Filter());
        var entry = data.Items.Single(x => x.ContentLogId == kkItem.Id);
        Assert.Equal(0.010m, entry.BaselineEngagementRate);
        Assert.Equal(3.0m, entry.Multiplier);
        Assert.NotEqual(1.5m, entry.Multiplier);
    }

    // ------------------------------------------------------------------ Top-N selection

    [Fact]
    public async Task NonKk_Leaderboard_Is_Exactly_Top4_Of6_DistinctEr()
    {
        using var host = new Host();
        // Baseline 0.010 so multipliers are informative.
        host.LogWithEr(BaselineDay(1), NonKk, views: 1000, likes: 10);

        // 6 items, distinct ER: 0.001 .. 0.006 (ER DESC: 0.006, 0.005, 0.004, 0.003 | excluded: 0.002, 0.001)
        var er5 = host.LogWithEr(PeriodDay(1), NonKk, views: 1000, likes: 6);
        var er4 = host.LogWithEr(PeriodDay(2), NonKk, views: 1000, likes: 5);
        var er3 = host.LogWithEr(PeriodDay(3), NonKk, views: 1000, likes: 4);
        var er2 = host.LogWithEr(PeriodDay(4), NonKk, views: 1000, likes: 3);
        var er1 = host.LogWithEr(PeriodDay(5), NonKk, views: 1000, likes: 2);
        var er0 = host.LogWithEr(PeriodDay(6), NonKk, views: 1000, likes: 1);

        var data = await host.Service.BuildAsync(Filter());
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk);

        Assert.Equal(4, lb.Entries.Count);
        Assert.Equal(new[] { er5.Id, er4.Id, er3.Id, er2.Id }, lb.Entries.Select(e => e.ContentLogId).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, lb.Entries.Select(e => e.Rank).ToArray());
        Assert.DoesNotContain(lb.Entries, e => e.ContentLogId == er1.Id); // 5th excluded
        Assert.DoesNotContain(lb.Entries, e => e.ContentLogId == er0.Id); // 6th excluded
    }

    [Fact]
    public async Task Kk_Leaderboard_Is_Exactly_Top4_Of6_DistinctEr()
    {
        using var host = new Host();
        host.LogWithEr(BaselineDay(1), Kk, views: 1000, likes: 10);

        var e1 = host.LogWithEr(PeriodDay(1), Kk, views: 1000, likes: 6);
        var e2 = host.LogWithEr(PeriodDay(2), Kk, views: 1000, likes: 5);
        var e3 = host.LogWithEr(PeriodDay(3), Kk, views: 1000, likes: 4);
        var e4 = host.LogWithEr(PeriodDay(4), Kk, views: 1000, likes: 3);
        var e5 = host.LogWithEr(PeriodDay(5), Kk, views: 1000, likes: 2);
        var e6 = host.LogWithEr(PeriodDay(6), Kk, views: 1000, likes: 1);

        var data = await host.Service.BuildAsync(Filter());
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == Kk);

        Assert.Equal(4, lb.Entries.Count);
        Assert.Equal(new[] { e1.Id, e2.Id, e3.Id, e4.Id }, lb.Entries.Select(x => x.ContentLogId).ToArray());
        Assert.DoesNotContain(lb.Entries, x => x.ContentLogId == e5.Id);
        Assert.DoesNotContain(lb.Entries, x => x.ContentLogId == e6.Id);
    }

    [Fact]
    public async Task AutoGmv_Leaderboard_Is_Exactly_Top2_Of4()
    {
        using var host = new Host();
        host.LogWithEr(BaselineDay(1), AutoGmv, views: 1000, likes: 10);

        var e1 = host.LogWithEr(PeriodDay(1), AutoGmv, views: 1000, likes: 4);
        var e2 = host.LogWithEr(PeriodDay(2), AutoGmv, views: 1000, likes: 3);
        var e3 = host.LogWithEr(PeriodDay(3), AutoGmv, views: 1000, likes: 2);
        var e4 = host.LogWithEr(PeriodDay(4), AutoGmv, views: 1000, likes: 1);

        var data = await host.Service.BuildAsync(Filter());
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == AutoGmv);

        Assert.Equal(2, lb.Entries.Count);
        Assert.Equal(new[] { e1.Id, e2.Id }, lb.Entries.Select(x => x.ContentLogId).ToArray());
        Assert.DoesNotContain(lb.Entries, x => x.ContentLogId == e3.Id);
        Assert.DoesNotContain(lb.Entries, x => x.ContentLogId == e4.Id);
    }

    [Fact]
    public async Task Archived_AutoGmv_Still_Eligible_And_Can_Rank_First()
    {
        using var host = new Host();
        host.LogWithEr(BaselineDay(1), AutoGmv, views: 1000, likes: 10);

        // Archived but highest ER - must appear (locked rule 13), IncludeArchived=false default.
        var archivedTop = host.LogWithEr(PeriodDay(1), AutoGmv, views: 1000, likes: 9, archived: true);
        var normal = host.LogWithEr(PeriodDay(2), AutoGmv, views: 1000, likes: 3);

        var data = await host.Service.BuildAsync(Filter());
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == AutoGmv);

        Assert.Equal(2, lb.Entries.Count);
        Assert.Equal(archivedTop.Id, lb.Entries[0].ContentLogId); // rank 1
        Assert.Equal(normal.Id, lb.Entries[1].ContentLogId);
    }

    // ------------------------------------------------------------------ tie-breaking determinism

    [Fact]
    public async Task Equal_Er_TieBreaks_By_ContentLogId_Desc_Deterministically()
    {
        using var host = new Host();
        host.LogWithEr(BaselineDay(1), NonKk, views: 1000, likes: 10);

        // Two items with EXACTLY the same ER (13/1000). Id order is insertion order.
        var first = host.LogWithEr(PeriodDay(1), NonKk, views: 1000, likes: 13);
        var second = host.LogWithEr(PeriodDay(2), NonKk, views: 1000, likes: 13);
        Assert.True(second.Id > first.Id);

        var data = await host.Service.BuildAsync(Filter());
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk);

        Assert.Equal(2, lb.Entries.Count);
        Assert.Equal(1, lb.Entries[0].Rank);
        Assert.Equal(second.Id, lb.Entries[0].ContentLogId); // higher Id wins the tie
        Assert.Equal(first.Id, lb.Entries[1].ContentLogId);

        // Deterministic across repeated executions.
        var data2 = await host.Service.BuildAsync(Filter());
        var lb2 = data2.Leaderboards.Single(l => l.ContentTypeCode == NonKk);
        Assert.Equal(lb.Entries.Select(e => e.ContentLogId), lb2.Entries.Select(e => e.ContentLogId));
    }

    // ------------------------------------------------------------------ category isolation

    [Fact]
    public async Task Extreme_Er_In_One_Category_Does_Not_Displace_Others()
    {
        using var host = new Host();
        host.LogWithEr(BaselineDay(1), NonKk, views: 1000, likes: 10);
        host.LogWithEr(BaselineDay(1), Kk, views: 1000, likes: 10);
        host.LogWithEr(BaselineDay(1), AutoGmv, views: 1000, likes: 10);

        // NON_KK: normal ERs.
        var nonKk1 = host.LogWithEr(PeriodDay(1), NonKk, views: 1000, likes: 20);
        var nonKk2 = host.LogWithEr(PeriodDay(2), NonKk, views: 1000, likes: 15);
        // KK: one EXTREMELY high ER.
        var kkExtreme = host.LogWithEr(PeriodDay(1), Kk, views: 1000, likes: 900);
        var kkNormal = host.LogWithEr(PeriodDay(2), Kk, views: 1000, likes: 10);
        // AUTO_GMV_LIVE: one EXTREMELY high ER.
        var autoGmvExtreme = host.LogWithEr(PeriodDay(1), AutoGmv, views: 1000, likes: 500);
        var autoGmvNormal = host.LogWithEr(PeriodDay(2), AutoGmv, views: 1000, likes: 5);

        var data = await host.Service.BuildAsync(Filter());

        var nonKkLb = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk);
        Assert.Equal(new[] { nonKk1.Id, nonKk2.Id }, nonKkLb.Entries.Select(e => e.ContentLogId).ToArray());
        Assert.All(nonKkLb.Entries, e => Assert.True(e.EngagementRate < 0.05m)); // untouched by extremes

        var kkLb = data.Leaderboards.Single(l => l.ContentTypeCode == Kk);
        Assert.Equal(kkExtreme.Id, kkLb.Entries[0].ContentLogId);
        Assert.Equal(kkNormal.Id, kkLb.Entries[1].ContentLogId);

        var autoGmvLb = data.Leaderboards.Single(l => l.ContentTypeCode == AutoGmv);
        Assert.Equal(autoGmvExtreme.Id, autoGmvLb.Entries[0].ContentLogId);
        Assert.Equal(autoGmvNormal.Id, autoGmvLb.Entries[1].ContentLogId);
    }

    // ------------------------------------------------------------------ CRITICAL: no multiplier threshold

    [Fact]
    public async Task No_Multiplier_Threshold_Sub1x_Items_Still_Rank()
    {
        using var host = new Host();
        // Baseline ER = 0.010.
        host.LogWithEr(BaselineDay(1), NonKk, views: 1000, likes: 10);

        // Top 5 ERs DESC give multipliers 1.1x, 1.0x, 0.9x, 0.8x, 0.7x (6th: 0.6x).
        var m11 = host.LogWithEr(PeriodDay(1), NonKk, views: 1000, likes: 11);
        var m10 = host.LogWithEr(PeriodDay(2), NonKk, views: 1000, likes: 10);
        var m09 = host.LogWithEr(PeriodDay(3), NonKk, views: 1000, likes: 9);
        var m08 = host.LogWithEr(PeriodDay(4), NonKk, views: 1000, likes: 8);
        var m07 = host.LogWithEr(PeriodDay(5), NonKk, views: 1000, likes: 7);
        var m06 = host.LogWithEr(PeriodDay(6), NonKk, views: 1000, likes: 6);

        var data = await host.Service.BuildAsync(Filter());
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk);

        // Top 4 selected PURELY by ER, regardless of multiplier values.
        Assert.Equal(4, lb.Entries.Count);
        Assert.Equal(new[] { m11.Id, m10.Id, m09.Id, m08.Id }, lb.Entries.Select(e => e.ContentLogId).ToArray());

        // Two of the four winners are BELOW 1.0x - proof that no >= 1.0 threshold exists.
        Assert.Contains(lb.Entries, e => e.ContentLogId == m09.Id && e.Multiplier == 0.9m);
        Assert.Contains(lb.Entries, e => e.ContentLogId == m08.Id && e.Multiplier == 0.8m);

        // A 1.5x threshold would leave 1 entry; a 1.0x threshold would leave 2. We have 4.
        Assert.All(lb.Entries, e => Assert.True(e.Multiplier < 1.5m));
    }

    [Fact]
    public async Task No_1_5x_And_No_1_8x_Threshold_Regression()
    {
        using var host = new Host();
        host.LogWithEr(BaselineDay(1), NonKk, views: 1000, likes: 10);

        // Multipliers: 1.6x, 1.4x, 1.3x, 1.2x, 1.1x - only the first exceeds 1.5x,
        // NONE exceeds 1.8x. If either threshold existed, fewer than 4 entries would return.
        var m16 = host.LogWithEr(PeriodDay(1), NonKk, views: 1000, likes: 16);
        var m14 = host.LogWithEr(PeriodDay(2), NonKk, views: 1000, likes: 14);
        var m13 = host.LogWithEr(PeriodDay(3), NonKk, views: 1000, likes: 13);
        var m12 = host.LogWithEr(PeriodDay(4), NonKk, views: 1000, likes: 12);
        var m11 = host.LogWithEr(PeriodDay(5), NonKk, views: 1000, likes: 11);

        var data = await host.Service.BuildAsync(Filter());
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk);

        Assert.Equal(4, lb.Entries.Count); // a 1.5x or 1.8x threshold would return 1 or 0
        Assert.Equal(new[] { m16.Id, m14.Id, m13.Id, m12.Id }, lb.Entries.Select(e => e.ContentLogId).ToArray());
        Assert.Contains(lb.Entries, e => e.ContentLogId == m14.Id && e.Multiplier == 1.4m); // below 1.5x, still ranked
        Assert.All(lb.Entries, e => Assert.True(e.Multiplier < 1.8m));
    }

    // ------------------------------------------------------------------ ranking hygiene

    [Fact]
    public async Task Items_Without_Valid_Er_Are_Never_Ranked()
    {
        using var host = new Host();
        host.LogWithEr(BaselineDay(1), NonKk, views: 1000, likes: 10);

        var valid = host.LogWithEr(PeriodDay(1), NonKk, views: 1000, likes: 20);
        var zeroViews = host.LogWithEr(PeriodDay(2), NonKk, views: 0, likes: 50);  // ER NULL (Views=0)
        var noMetric = host.Log(PeriodDay(3), NonKk);                              // ER NULL (no metric)

        var data = await host.Service.BuildAsync(Filter());
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk);

        var entry = Assert.Single(lb.Entries);
        Assert.Equal(valid.Id, entry.ContentLogId);
        Assert.DoesNotContain(lb.Entries, e => e.ContentLogId == zeroViews.Id);
        Assert.DoesNotContain(lb.Entries, e => e.ContentLogId == noMetric.Id);
    }

    [Fact]
    public async Task Leaderboard_Entry_Carries_Mockup_Fields()
    {
        using var host = new Host();
        host.LogWithEr(BaselineDay(1), NonKk, views: 1000, likes: 10);
        var item = host.LogWithEr(PeriodDay(1), NonKk, views: 2000, likes: 60, comments: 20, shares: 20);

        var data = await host.Service.BuildAsync(Filter());
        var entry = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk).Entries.Single();

        Assert.Equal(item.Id, entry.ContentLogId);
        Assert.False(string.IsNullOrEmpty(entry.VideoId));
        Assert.False(string.IsNullOrEmpty(entry.Title));
        Assert.False(string.IsNullOrEmpty(entry.Username));
        Assert.False(string.IsNullOrEmpty(entry.VideoUrl));
        Assert.Equal(NonKk, entry.ContentTypeCode);
        Assert.Equal(2000, entry.Views);
        Assert.Equal(60, entry.Likes);
        Assert.Equal(20, entry.Comments);
        Assert.Equal(20, entry.Shares);
        Assert.Equal(0.05m, entry.EngagementRate);        // (60+20+20)/2000
        Assert.Equal(0.010m, entry.BaselineEngagementRate);
        Assert.Equal(5.0m, entry.Multiplier);             // 0.05 / 0.01
        Assert.NotNull(entry.LatestMetricCapturedAt);
        Assert.Equal(1, entry.Rank);
    }

    [Fact]
    public async Task Leaderboard_Uses_PerVideo_Average_Baseline_From_Phase2A()
    {
        using var host = new Host();
        // Baseline videos: ER 0.010 and 0.020 -> baseline AVG = 0.015 (NOT aggregate 30/2100).
        host.LogWithEr(BaselineDay(1), NonKk, views: 1000, likes: 10);
        host.LogWithEr(BaselineDay(2), NonKk, views: 100, likes: 2);
        var item = host.LogWithEr(PeriodDay(1), NonKk, views: 1000, likes: 30);

        var data = await host.Service.BuildAsync(Filter());
        var lb = data.Leaderboards.Single(l => l.ContentTypeCode == NonKk);

        Assert.Equal(2, lb.BaselineVideoCount);
        Assert.Equal(0.015m, lb.BaselineEngagementRate);
        Assert.NotEqual(30m / 2100m, lb.BaselineEngagementRate);

        var entry = lb.Entries.Single(e => e.ContentLogId == item.Id);
        Assert.Equal(2.0m, entry.Multiplier);             // 0.030 / 0.015
    }
}
