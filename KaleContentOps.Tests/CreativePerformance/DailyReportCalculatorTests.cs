using System;
using System.Collections.Generic;
using System.Linq;
using KaleContentOps.Services.CreativePerformance;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// Locked formulas (spec section D) plus null/zero honesty rules:
/// null stays null, valid zero stays zero, no divide-by-zero anywhere.
/// </summary>
public class DailyReportCalculatorTests
{
    // ---------------- Interactions = Likes + Comments + Shares ----------------

    [Fact]
    public void Interactions_SumsAllComponents()
    {
        Assert.Equal(1000L, DailyReportCalculator.Interactions(800, 80, 120));
    }

    [Fact]
    public void Interactions_ZeroComponents_IsValidZero()
    {
        Assert.Equal(0L, DailyReportCalculator.Interactions(0, 0, 0));
    }

    [Theory]
    [InlineData(null, 5L, 5L)]
    [InlineData(5L, null, 5L)]
    [InlineData(5L, 5L, null)]
    public void Interactions_AnyNullComponent_YieldsNull_NeverPartialSum(long? likes, long? comments, long? shares)
    {
        Assert.Null(DailyReportCalculator.Interactions(likes, comments, shares));
    }

    // ---------------- Engagement Rate = Interactions / Reach x 100 ----------------

    [Fact]
    public void EngagementRate_MatchesSpecExample_1304Percent()
    {
        // Mockup: Interactions 9.173 / Reach 70.334 = 13.04% (rounded for display)
        var er = DailyReportCalculator.EngagementRate(9173, 70334);

        Assert.NotNull(er);
        Assert.Equal(13.0421m, Math.Round(er!.Value, 4));
    }

    [Fact]
    public void EngagementRate_ZeroInteractionsWithReach_IsZero_NotNull()
    {
        Assert.Equal(0m, DailyReportCalculator.EngagementRate(0, 1000));
    }

    [Theory]
    [InlineData(null, 1000L)]  // no interactions recorded
    [InlineData(100L, null)]   // no reach recorded
    [InlineData(100L, 0L)]     // zero reach: division by zero must not happen
    [InlineData(100L, -5L)]    // negative reach is nonsense, treat as unavailable
    public void EngagementRate_MissingOrZeroDenominator_YieldsNull(long? interactions, long? reach)
    {
        Assert.Null(DailyReportCalculator.EngagementRate(interactions, reach));
    }

    [Fact]
    public void PeriodEngagementRate_IsTotalsRatio_NotAverageOfDailyRates()
    {
        // Day A: 50/100 = 50%. Day B: 0/900 = 0%. Period = 50/1000 = 5%,
        // NOT the average of 50% and 0% (which would be 25%).
        Assert.Equal(5m, DailyReportCalculator.PeriodEngagementRate(50, 1000));
    }

    // ---------------- Difference / Growth ----------------

    [Fact]
    public void Difference_IsCurrentMinusPrevious()
    {
        Assert.Equal(13903m, DailyReportCalculator.Difference(70334m, 56431m));
        Assert.Equal(-1546m, DailyReportCalculator.Difference(5620m, 7166m));
        Assert.Equal(0m, DailyReportCalculator.Difference(5m, 5m));
    }

    [Fact]
    public void Difference_NullOnEitherSide()
    {
        Assert.Null(DailyReportCalculator.Difference(null, 5m));
        Assert.Null(DailyReportCalculator.Difference(5m, null));
        Assert.Null(DailyReportCalculator.Difference(null, null));
    }

    [Fact]
    public void GrowthPercentage_MatchesSpecExample_246Percent()
    {
        // Mockup: (70.334 - 56.431) / 56.431 x 100 = +24.6%
        var growth = DailyReportCalculator.GrowthPercentage(70334m, 56431m);

        Assert.NotNull(growth);
        Assert.Equal(24.6372m, Math.Round(growth!.Value, 4));
    }

    [Fact]
    public void GrowthPercentage_ZeroPrevious_YieldsNull_NoDivisionByZero()
    {
        Assert.Null(DailyReportCalculator.GrowthPercentage(100m, 0m));
    }

    [Fact]
    public void GrowthPercentage_NullSide_YieldsNull()
    {
        Assert.Null(DailyReportCalculator.GrowthPercentage(null, 5m));
        Assert.Null(DailyReportCalculator.GrowthPercentage(5m, null));
    }

    // ---------------- Net Follower = Gained - Lost ----------------

    [Fact]
    public void NetFollower_GainedMinusLost()
    {
        Assert.Equal(115L, DailyReportCalculator.NetFollower(320, 205));
        Assert.Equal(-14L, DailyReportCalculator.NetFollower(0, 14));
        Assert.Equal(0L, DailyReportCalculator.NetFollower(0, 0));
    }

    [Fact]
    public void NetFollower_NullComponent_YieldsNull()
    {
        Assert.Null(DailyReportCalculator.NetFollower(null, 5));
        Assert.Null(DailyReportCalculator.NetFollower(5, null));
    }

    // ---------------- Composition percentage ----------------

    [Fact]
    public void CompositionPercentage_PartOverTotal()
    {
        // Mockup donut: Like 7.432 / Interactions 9.173 = 81%
        Assert.Equal(81.0204m, Math.Round(DailyReportCalculator.CompositionPercentage(7432m, 9173m)!.Value, 4));
    }

    [Fact]
    public void CompositionPercentage_ZeroTotal_YieldsNull()
    {
        Assert.Null(DailyReportCalculator.CompositionPercentage(5m, 0m));
        Assert.Null(DailyReportCalculator.CompositionPercentage(5m, null));
        Assert.Null(DailyReportCalculator.CompositionPercentage(null, 10m));
    }

    // ---------------- Sum + completeness ----------------

    [Fact]
    public void Sum_FullCoverage_IsComplete()
    {
        var agg = DailyReportCalculator.Sum(new long?[] { 1, 2, 3, 4 }, 4);

        Assert.Equal(10L, agg.Value);
        Assert.Equal(4, agg.DaysCovered);
        Assert.Equal(DailyMetricStatus.Complete, agg.Status);
    }

    [Fact]
    public void Sum_MissingDays_ArePartial_NotTreatedAsZero()
    {
        // 2 of 4 days covered: the sum is 3, but the status is Partial so the UI
        // can never present it as a full-period total.
        var agg = DailyReportCalculator.Sum(new long?[] { 1, null, 2, null }, 4);

        Assert.Equal(3L, agg.Value);
        Assert.Equal(2, agg.DaysCovered);
        Assert.Equal(DailyMetricStatus.Partial, agg.Status);
    }

    [Fact]
    public void Sum_ZeroValues_AreCoveredDays_NotNoData()
    {
        var agg = DailyReportCalculator.Sum(new long?[] { 0, 0, 0 }, 3);

        Assert.Equal(0L, agg.Value);
        Assert.Equal(3, agg.DaysCovered);
        Assert.Equal(DailyMetricStatus.Complete, agg.Status);
    }

    [Fact]
    public void Sum_NoValues_IsNoData()
    {
        var agg = DailyReportCalculator.Sum(new long?[] { null, null }, 2);

        Assert.Null(agg.Value);
        Assert.Equal(DailyMetricStatus.NoData, agg.Status);
    }

    [Fact]
    public void Worst_ChoosesWeakestStatus()
    {
        Assert.Equal(DailyMetricStatus.Partial, DailyReportCalculator.Worst(DailyMetricStatus.Complete, DailyMetricStatus.Partial));
        Assert.Equal(DailyMetricStatus.NoData, DailyReportCalculator.Worst(DailyMetricStatus.Partial, DailyMetricStatus.NoData));
        Assert.Equal(DailyMetricStatus.Unavailable, DailyReportCalculator.Worst(DailyMetricStatus.Complete, DailyMetricStatus.Unavailable));
        Assert.Equal(DailyMetricStatus.Complete, DailyReportCalculator.Worst(DailyMetricStatus.Complete, DailyMetricStatus.Complete));
    }

    // ---------------- Composition gating ----------------

    [Fact]
    public void CanRenderComposition_RequiresAllCompleteAndPositiveTotal()
    {
        Assert.True(DailyReportCalculator.CanRenderComposition(
            new[] { DailyMetricStatus.Complete, DailyMetricStatus.Complete, DailyMetricStatus.Complete }, 100m));

        Assert.False(DailyReportCalculator.CanRenderComposition(
            new[] { DailyMetricStatus.Complete, DailyMetricStatus.Partial, DailyMetricStatus.Complete }, 100m));

        Assert.False(DailyReportCalculator.CanRenderComposition(
            new[] { DailyMetricStatus.Complete, DailyMetricStatus.Complete, DailyMetricStatus.Complete }, 0m));

        Assert.False(DailyReportCalculator.CanRenderComposition(
            new[] { DailyMetricStatus.Complete, DailyMetricStatus.Complete, DailyMetricStatus.Complete }, null));
    }

    // ---------------- Follower snapshot ----------------

    [Fact]
    public void FollowerSnapshot_PicksLatestOnOrBeforePeriodEnd()
    {
        var snapshots = new List<FollowerSnapshot>
        {
            new() { AsOfDate = new DateTime(2026, 8, 1), FollowerCount = 19000 },
            new() { AsOfDate = new DateTime(2026, 9, 5), FollowerCount = 20177 },
            new() { AsOfDate = new DateTime(2026, 9, 20), FollowerCount = 21000 } // after EndDate
        };

        var picked = DailyReportCalculator.FollowerSnapshotAtOrBefore(snapshots, new DateTime(2026, 9, 5));

        Assert.NotNull(picked);
        Assert.Equal(20177L, picked!.FollowerCount);
        Assert.Equal(new DateTime(2026, 9, 5), picked.AsOfDate);
    }

    [Fact]
    public void FollowerSnapshot_NeverUsesSnapshotAfterEndDate()
    {
        var snapshots = new List<FollowerSnapshot>
        {
            new() { AsOfDate = new DateTime(2026, 9, 20), FollowerCount = 21000 }
        };

        Assert.Null(DailyReportCalculator.FollowerSnapshotAtOrBefore(snapshots, new DateTime(2026, 9, 5)));
    }

    [Fact]
    public void FollowerSnapshot_SameDate_UsesLatestCapture()
    {
        var snapshots = new List<FollowerSnapshot>
        {
            new() { AsOfDate = new DateTime(2026, 9, 5), FollowerCount = 20100, CapturedAt = new DateTime(2026, 9, 5, 3, 0, 0) },
            new() { AsOfDate = new DateTime(2026, 9, 5), FollowerCount = 20177, CapturedAt = new DateTime(2026, 9, 5, 15, 0, 0) }
        };

        var picked = DailyReportCalculator.FollowerSnapshotAtOrBefore(snapshots, new DateTime(2026, 9, 5));

        Assert.Equal(20177L, picked!.FollowerCount);
    }

    [Fact]
    public void FollowerSnapshot_EmptyOrNull_YieldsNull()
    {
        Assert.Null(DailyReportCalculator.FollowerSnapshotAtOrBefore(null, new DateTime(2026, 9, 5)));
        Assert.Null(DailyReportCalculator.FollowerSnapshotAtOrBefore(Enumerable.Empty<FollowerSnapshot>(), new DateTime(2026, 9, 5)));
    }
}
