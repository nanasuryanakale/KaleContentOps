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
/// Phase D tests for Winning Content Features 2-5 (no locked business rule changes):
/// - Feature 2: production-method breakdown over NON_KK + KK (Auto GMV Live excluded),
///   AI/Self mapping (NULL method displays as Self Produce), median rule UNCHANGED
///   (strict &lt; median comes straight from the existing BelowMedian groups),
///   correct totals/percentages.
/// - Feature 3: AI vs Self composition (KK+Non-KK denominator) and performance values
///   (average Views, average per-video ER) - no mockup sample numbers anywhere.
/// - Feature 4: funnel averages per video and percentages relative to Views; Views = 0,
///   NULL metrics are safe; decimal math only (never NaN/Infinity).
/// - Feature 5: audience snapshot uses the EXISTING baseline window [start-30d, start)
///   as the previous period; deltas are PERCENTAGE POINTS; missing demographics stay
///   NULL; Follower % renders as explicitly unavailable (no fabricated percentage).
/// </summary>
public class WinningContentFeaturesTests
{
    private const string NonKk = "NON_KK";
    private const string Kk = "KK";
    private const string AutoGmv = "AUTO_GMV_LIVE";
    private const string Ai = "AI_PRODUCE";
    private const string Self = "SELF_PRODUCE";

    private sealed class Host : IDisposable
    {
        public AppDbContext Db { get; }
        public WinningContentService Service { get; }
        public int NonKkId { get; }
        public int KkId { get; }
        public int AutoGmvId { get; }
        public int AiId { get; }
        public int SelfId { get; }

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
            Db.ProductionMethods.AddRange(
                new ProductionMethod { Code = Ai, Name = "AI Produce" },
                new ProductionMethod { Code = Self, Name = "Self Produce" });
            Db.SaveChanges();

            NonKkId = Db.ContentTypes.Single(c => c.Code == NonKk).Id;
            KkId = Db.ContentTypes.Single(c => c.Code == Kk).Id;
            AutoGmvId = Db.ContentTypes.Single(c => c.Code == AutoGmv).Id;
            AiId = Db.ProductionMethods.Single(p => p.Code == Ai).Id;
            SelfId = Db.ProductionMethods.Single(p => p.Code == Self).Id;

            Service = new WinningContentService(Db);
        }

        public ContentLog Log(DateTime postTime, string contentTypeCode, int? productionMethodId = null, string? title = null)
        {
            var log = new ContentLog
            {
                VideoId = Guid.NewGuid().ToString("N"),
                Title = title,
                VideoPostTime = postTime,
                ContentTypeId = Db.ContentTypes.Single(c => c.Code == contentTypeCode).Id,
                ProductionMethodId = productionMethodId
            };
            Db.ContentLogs.Add(log);
            Db.SaveChanges();
            return log;
        }

        public ContentMetric Metric(ContentLog log, long? views, long? likes, long? comments, long? shares,
            long? reach = null, string? demographicsJson = null, DateTime? capturedAt = null)
        {
            var metric = new ContentMetric
            {
                ContentLogId = log.Id,
                Views = views,
                Likes = likes,
                Comments = comments,
                Shares = shares,
                Reach = reach,
                DemographicsJson = demographicsJson,
                CapturedAt = capturedAt ?? DateTime.UtcNow
            };
            Db.ContentMetrics.Add(metric);
            Db.SaveChanges();
            return metric;
        }

        public void Dispose() => Db.Dispose();
    }

    private static WinningContentFilter Period(int year, int month, int day, int days) => new()
    {
        StartDate = new DateTime(year, month, day),
        EndDate = new DateTime(year, month, day).AddDays(days - 1)
    };

    private const string DemoBoth =
        "{\"male\":0.4,\"female\":0.6,\"ages\":{\"18-24\":0.3,\"25-34\":0.2}}";

    // ------------------------------------------------------------------ Feature 2

    [Fact]
    public async Task Feature2_Breakdown_Exists_With_Locked_Method_Catalog()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);
        host.Log(day, NonKk, host.AiId);
        host.Log(day, NonKk, host.SelfId);

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        Assert.Equal(new[] { Ai, Self }, data.ProductionBreakdown.Methods.Select(m => m.Code).ToArray());
    }

    [Fact]
    public async Task Feature2_Null_ProductionMethod_Displays_As_SelfProduce_No_New_Methods()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);
        host.Log(day, NonKk, productionMethodId: null);          // NULL -> Self Produce (Content Log convention)
        host.Log(day, Kk, productionMethodId: null);             // KK without method too

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        var ai = data.ProductionBreakdown.Methods.Single(m => m.Code == Ai);
        var self = data.ProductionBreakdown.Methods.Single(m => m.Code == Self);
        Assert.Equal(0, ai.TotalCount);
        Assert.Equal(2, self.TotalCount);
        Assert.Equal(2, data.ProductionBreakdown.TotalKkNonKkCount);
    }

    [Fact]
    public async Task Feature2_AutoGmvLive_Excluded_From_Breakdown()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);
        host.Log(day, AutoGmv, productionMethodId: null);        // Auto GMV Live never carries a method
        host.Log(day, NonKk, host.AiId);

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        Assert.Equal(1, data.ProductionBreakdown.TotalKkNonKkCount);
        Assert.Equal(1, data.ProductionBreakdown.Methods.Single(m => m.Code == Ai).TotalCount);
        Assert.Equal(0, data.ProductionBreakdown.Methods.Single(m => m.Code == Self).TotalCount);
    }

    [Fact]
    public async Task Feature2_BelowMedian_Uses_Existing_Strict_Median_Per_ContentType()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        // NON_KK views [100(AI), 100(Self), 200(Self), 300(Self)] -> median (100+200)/2 = 150.
        var aiLow = host.Log(day, NonKk, host.AiId);
        host.Metric(aiLow, 100, 1, 0, 0);                        // below 150
        var selfLow = host.Log(day, NonKk, host.SelfId);
        host.Metric(selfLow, 100, 1, 0, 0);                      // below 150
        var selfMid = host.Log(day, NonKk, host.SelfId);
        host.Metric(selfMid, 200, 1, 0, 0);                      // below 150? no: 200 > 150 -> NOT below
        var selfHigh = host.Log(day, NonKk, host.SelfId);
        host.Metric(selfHigh, 300, 1, 0, 0);                     // above

        // KK video without method -> counts as Self; views 500 above its own median (single value -> median 500).
        var kk = host.Log(day, Kk, null);
        host.Metric(kk, 500, 5, 0, 0);

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        var ai = data.ProductionBreakdown.Methods.Single(m => m.Code == Ai);
        var self = data.ProductionBreakdown.Methods.Single(m => m.Code == Self);

        Assert.Equal(1, ai.TotalCount);
        Assert.Equal(4, self.TotalCount);                        // 3 NON_KK + 1 KK
        Assert.Equal(1, ai.BelowMedianCount);
        Assert.Equal(1, self.BelowMedianCount);                  // only the 100-views Self video

        // Percentages over the method's own totals.
        Assert.Equal(100m, ai.BelowMedianPercentage);            // 1/1
        Assert.Equal(25m, self.BelowMedianPercentage);           // 1/4

        // Cross-check against the untouched backend groups (same membership).
        var belowIds = data.BelowMedian.SelectMany(g => g.Entries).Select(e => e.ContentLogId).ToHashSet();
        Assert.Contains(aiLow.Id, belowIds);
        Assert.Contains(selfLow.Id, belowIds);
        Assert.DoesNotContain(selfMid.Id, belowIds);             // strict < median preserved
        Assert.DoesNotContain(selfHigh.Id, belowIds);
    }

    // ------------------------------------------------------------------ Feature 3

    [Fact]
    public async Task Feature3_Composition_Performance_Values_Computed_From_Data()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        var ai1 = host.Log(day, NonKk, host.AiId);
        host.Metric(ai1, 1000, 50, 0, 0);                        // ER = 0.05
        var ai2 = host.Log(day, NonKk, host.AiId);
        host.Metric(ai2, 500, 15, 5, 5);                         // ER = 25/500 = 0.05
        var self1 = host.Log(day, Kk, host.SelfId);
        host.Metric(self1, 2000, 20, 0, 0);                      // ER = 0.01
        var self2 = host.Log(day, NonKk, host.SelfId);
        host.Metric(self2, 1000, 10, 0, 0);                      // ER = 0.01

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        var ai = data.ProductionBreakdown.Methods.Single(m => m.Code == Ai);
        var self = data.ProductionBreakdown.Methods.Single(m => m.Code == Self);

        Assert.Equal(2, ai.TotalCount);
        Assert.Equal(2, self.TotalCount);
        Assert.Equal(4, data.ProductionBreakdown.TotalKkNonKkCount);

        Assert.Equal(750m, ai.AverageViewsPerVideo);             // (1000+500)/2
        Assert.Equal(1500m, self.AverageViewsPerVideo);          // (2000+1000)/2
        Assert.Equal(0.05m, ai.AverageEngagementRate);           // avg of per-video ER (baseline semantics)
        Assert.Equal(0.01m, self.AverageEngagementRate);
    }

    [Fact]
    public async Task Feature3_No_Data_Produces_Nulls_Never_Zeros()
    {
        using var host = new Host();
        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        var ai = data.ProductionBreakdown.Methods.Single(m => m.Code == Ai);
        Assert.Equal(0, ai.TotalCount);
        Assert.Null(ai.AverageViewsPerVideo);
        Assert.Null(ai.AverageEngagementRate);
        Assert.Null(ai.BelowMedianPercentage);
    }

    // ------------------------------------------------------------------ Feature 4

    [Fact]
    public async Task Feature4_Funnel_Averages_And_Percentages()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        var v1 = host.Log(day, NonKk, host.SelfId);
        host.Metric(v1, 1000, 30, 5, 5, reach: 600);             // engagement 40
        var v2 = host.Log(day, NonKk, host.SelfId);
        host.Metric(v2, 500, 10, 5, 5, reach: 300);              // engagement 20

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        Assert.Equal(750m, data.Funnel.AverageViewsPerVideo);    // (1000+500)/2
        Assert.Equal(450m, data.Funnel.AverageReachPerVideo);    // (600+300)/2
        Assert.Equal(30m, data.Funnel.AverageEngagementPerVideo);// (40+20)/2 - existing ER numerator
        Assert.Equal(100m, data.Funnel.ViewsPercent);
        Assert.Equal(60m, data.Funnel.ReachPercent);             // 450/750
        Assert.Equal(4m, data.Funnel.EngagementPercent);         // 30/750
        Assert.Equal(2, data.Funnel.VideosWithViews);
        Assert.Equal(2, data.Funnel.VideosWithReach);
        Assert.Equal(2, data.Funnel.VideosWithEngagement);
    }

    [Fact]
    public async Task Feature4_Views_Zero_And_Null_Metrics_Are_Safe_No_NaN()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        var zeroViews = host.Log(day, NonKk, host.SelfId);
        host.Metric(zeroViews, 0, 5, 0, 0, reach: 10);           // Views = 0: ER null (existing), funnel safe
        var noReach = host.Log(day, NonKk, host.SelfId);
        host.Metric(noReach, 100, 1, 0, 0, reach: null);         // Reach NULL excluded from reach avg
        var nullEngagement = host.Log(day, NonKk, host.SelfId);
        host.Metric(nullEngagement, 200, null, 0, 0, reach: 50); // Likes NULL -> no engagement sum

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        Assert.Equal(100m, data.Funnel.AverageViewsPerVideo);    // (0+100+200)/3 - zero counts as a real value
        Assert.Equal(30m, data.Funnel.AverageReachPerVideo);     // (10+50)/2 - NULL reach excluded
        Assert.Equal(3m, data.Funnel.AverageEngagementPerVideo); // (5+1)/2 - zeroViews has a complete engagement set

        // Views avg > 0 so percentages exist; all finite decimals (decimal math: no NaN/Infinity possible).
        Assert.Equal(100m, data.Funnel.ViewsPercent);
        Assert.True(data.Funnel.ReachPercent!.Value >= 0m);
        Assert.Equal(3m, data.Funnel.EngagementPercent);         // 3/100
    }

    [Fact]
    public void Feature4_Calculator_Views_Zero_Yields_Null_Percent_Never_DivideByZero()
    {
        var (views, reach, engagement) = WinningContentCalculator.ComputeFunnelAverages(new[]
        {
            (Views: (long?)0, Reach: (long?)0, Likes: (long?)0, Comments: (long?)0, Shares: (long?)0)
        });

        Assert.Equal(0m, views);
        Assert.Equal(0m, reach);
        Assert.Equal(0m, engagement);
        Assert.Null(WinningContentCalculator.ComputeFunnelPercent(reach, views));   // 0/0 -> NULL
        Assert.Null(WinningContentCalculator.ComputeFunnelPercent(10m, 0m));        // stage/0 -> NULL
        Assert.Null(WinningContentCalculator.ComputeFunnelPercent(null, 100m));     // NULL stage -> NULL
    }

    // ------------------------------------------------------------------ Feature 5

    [Fact]
    public async Task Feature5_Current_And_Previous_Period_Weighted_Shares_With_Point_Deltas()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        // Current period: weighted male = (0.4*1000 + 0.6*500)/1500 = 46.67%,
        // female = 53.33%, age18_34 = (0.5*1000 + 0.2*500)/1500 = 40%.
        var c1 = host.Log(day, NonKk, host.SelfId);
        host.Metric(c1, 1000, 5, 0, 0, demographicsJson:
            "{\"male\":0.4,\"female\":0.6,\"ages\":{\"18-24\":0.3,\"25-34\":0.2}}");
        var c2 = host.Log(day, NonKk, host.SelfId);
        host.Metric(c2, 500, 2, 0, 0, demographicsJson:
            "{\"male\":0.6,\"female\":0.4,\"ages\":{\"18-24\":0.1,\"25-34\":0.1}}");

        // Previous period = the EXISTING baseline window [start-30d, start) = [2026-08-02, 2026-09-01).
        var p1 = host.Log(new DateTime(2026, 8, 20, 12, 0, 0), NonKk, host.SelfId);
        host.Metric(p1, 200, 1, 0, 0, demographicsJson:
            "{\"male\":0.3,\"female\":0.7,\"ages\":{\"18-24\":0.2,\"25-34\":0.1}}");

        var data = await host.Service.BuildAsync(Period(2026, 9, 10, 7));

        Assert.Equal(new DateTime(2026, 8, 11), data.Audience.PreviousStart);   // 2026-09-10 - 30d
        Assert.Equal(new DateTime(2026, 9, 10), data.Audience.PreviousEndExclusive);

        var male = data.Audience.Metrics.Single(m => m.Key == "male");
        var female = data.Audience.Metrics.Single(m => m.Key == "female");
        var age = data.Audience.Metrics.Single(m => m.Key == "age18_34");

        // Full-precision decimals are compared at 4 dp (repeating expansions).
        Assert.Equal(46.6667m, Math.Round(male.CurrentPercentage!.Value, 4));
        Assert.Equal(30m, male.PreviousPercentage);
        Assert.Equal(16.6667m, Math.Round(male.DeltaPoints!.Value, 4));     // POINTS, not relative growth

        Assert.Equal(53.3333m, Math.Round(female.CurrentPercentage!.Value, 4));
        Assert.Equal(70m, female.PreviousPercentage);
        Assert.Equal(-16.6667m, Math.Round(female.DeltaPoints!.Value, 4));  // negative delta supported

        Assert.Equal(40m, age.CurrentPercentage);
        Assert.Equal(30m, age.PreviousPercentage);
        Assert.Equal(10m, age.DeltaPoints);

        Assert.Equal(2, data.Audience.CurrentVideosWithDemographics);
        Assert.Equal(1, data.Audience.PreviousVideosWithDemographics);
    }

    [Fact]
    public async Task Feature5_Baseline_Window_Is_Reused_Not_A_Second_Window()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);
        var c = host.Log(day, NonKk, host.SelfId);
        host.Metric(c, 100, 5, 0, 0, demographicsJson: DemoBoth);

        // Just OUTSIDE [2026-08-11, 2026-09-10): must NOT leak into the previous period.
        var tooEarly = host.Log(new DateTime(2026, 8, 9, 23, 59, 59), NonKk, host.SelfId);
        host.Metric(tooEarly, 100, 1, 0, 0, demographicsJson: DemoBoth);
        var atStart = host.Log(new DateTime(2026, 9, 10, 0, 0, 0), NonKk, host.SelfId);
        host.Metric(atStart, 100, 1, 0, 0, demographicsJson: DemoBoth);   // belongs to CURRENT period

        var data = await host.Service.BuildAsync(Period(2026, 9, 10, 1));

        Assert.Equal(2, data.Audience.CurrentVideosWithDemographics);     // day + atStart
        Assert.Equal(0, data.Audience.PreviousVideosWithDemographics);
        var male = data.Audience.Metrics.Single(m => m.Key == "male");
        Assert.Equal(40m, male.CurrentPercentage);
        Assert.Null(male.PreviousPercentage);
        Assert.Null(male.DeltaPoints);                                    // missing previous -> no fabricated delta
    }

    [Fact]
    public async Task Feature5_Missing_And_Corrupt_Demographics_Stay_Null()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);

        var noDemo = host.Log(day, NonKk, host.SelfId);
        host.Metric(noDemo, 1000, 5, 0, 0, demographicsJson: null);
        var corrupt = host.Log(day, NonKk, host.SelfId);
        host.Metric(corrupt, 1000, 5, 0, 0, demographicsJson: "{not-valid-json");
        var zeroViews = host.Log(day, NonKk, host.SelfId);
        host.Metric(zeroViews, 0, 5, 0, 0, demographicsJson: DemoBoth);   // 0 views -> never contributes

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        Assert.Equal(0, data.Audience.CurrentVideosWithDemographics);
        var male = data.Audience.Metrics.Single(m => m.Key == "male");
        Assert.Null(male.CurrentPercentage);
        Assert.Null(male.DeltaPoints);
    }

    [Fact]
    public void Feature5_Demographics_Are_0_1_Scale_Age_Summed_Per_Mockup()
    {
        var (male, female, age) = WinningContentService.ParseDemographicShares(
            "{\"male\":0.4,\"female\":0.6,\"ages\":{\"18-24\":0.3,\"25-34\":0.2}}");

        Assert.Equal(0.4m, male);
        Assert.Equal(0.6m, female);
        Assert.Equal(0.5m, age);                    // 18-24 + 25-34

        var (male2, female2, age2) = WinningContentService.ParseDemographicShares(
            "{\"male\":0.3,\"ages\":{\"18-24\":0.4}}");
        Assert.Equal(0.3m, male2);
        Assert.Null(female2);                       // absent key stays NULL
        Assert.Equal(0.4m, age2);                   // single bucket still sums
    }

    [Fact]
    public async Task Feature5_Follower_Percentage_Is_Not_Available_And_Never_Fabricated()
    {
        using var host = new Host();
        var day = new DateTime(2026, 9, 10, 12, 0, 0);
        var log = host.Log(day, NonKk, host.SelfId);
        // Even WITH NewFollowers ingested, there is no follower-SHARE metric:
        host.Metric(log, 1000, 5, 0, 0, demographicsJson: DemoBoth);
        host.Db.ContentMetrics.First().NewFollowers = 12;
        host.Db.SaveChanges();

        var data = await host.Service.BuildAsync(Period(2026, 9, 1, 30));

        var follower = data.Audience.Metrics.Single(m => m.Key == "follower");
        Assert.False(follower.IsAvailable);
        Assert.Null(follower.CurrentPercentage);
        Assert.Null(follower.PreviousPercentage);
        Assert.Null(follower.DeltaPoints);
        Assert.False(string.IsNullOrWhiteSpace(follower.UnavailableReason));
    }

    // ------------------------------------------------------------------ calculator edge cases

    [Fact]
    public void Feature5_DeltaPoints_Is_Percentage_Point_Difference()
    {
        Assert.Equal(2.7m, WinningContentCalculator.ComputeDeltaPoints(52.7m, 50.0m));
        Assert.Equal(-2.4m, WinningContentCalculator.ComputeDeltaPoints(40.6m, 43.0m));
        Assert.Null(WinningContentCalculator.ComputeDeltaPoints(null, 50m));
        Assert.Null(WinningContentCalculator.ComputeDeltaPoints(50m, null));
    }

    [Fact]
    public void Feature5_WeightedShare_Excludes_Null_Shares_And_Zero_Views()
    {
        var pct = WinningContentCalculator.ComputeWeightedSharePercent(new[]
        {
            (Views: (long?)1000, Share: (decimal?)0.4m),
            (Views: (long?)500, Share: (decimal?)null),   // no demographics -> excluded both sides
            (Views: (long?)0, Share: (decimal?)0.9m),     // 0 views -> excluded
            (Views: (long?)null, Share: (decimal?)0.5m)   // no metric -> excluded
        });

        Assert.Equal(40m, pct);                            // 0.4*1000 / 1000
        Assert.Null(WinningContentCalculator.ComputeWeightedSharePercent(new[]
        {
            (Views: (long?)1000, Share: (decimal?)null)
        }));
    }
}
