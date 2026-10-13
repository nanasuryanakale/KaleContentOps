using System;
using System.Collections.Generic;
using System.Linq;

namespace KaleContentOps.Services.CreativePerformance;

// ============================================================================
// Pure business formulas for the Creative Performance - Daily Report.
// No I/O, no database, no culture formatting - every function is deterministic
// and unit-testable with controlled inputs.
//
// Locked formulas (Daily Report spec, section D):
//   Interactions    = Likes + Comments + Shares (null component => null, never a
//                     partial sum presented as a complete total)
//   ER (daily)      = Interactions / Reach x 100    (Reach is the denominator -
//                     Winning Content keeps its own Views-based formula, untouched)
//   ER (period)     = Total Interactions / Total Reach x 100
//                     (NOT the average of the daily ERs)
//   Difference      = Current - Previous
//   Growth %        = (Current - Previous) / Previous x 100, null when Previous <= 0
//   Net Follower    = Followers Gained - Followers Lost (null component => null)
//   Composition %   = Part / Total x 100, null when Total is null or 0
// ============================================================================

/// <summary>Aggregated value of one metric over a period, with its completeness status.</summary>
public readonly record struct DailyMetricAggregate(
    long? Value,
    int DaysCovered,
    DailyMetricStatus Status)
{
    public static DailyMetricAggregate None(DailyMetricStatus status, int daysCovered = 0)
        => new(null, daysCovered, status);
}

public static class DailyReportCalculator
{
    /// <summary>Interactions = Likes + Comments + Shares. Null when ANY component is null (no partial totals).</summary>
    public static long? Interactions(long? likes, long? comments, long? shares)
    {
        if (likes is null || comments is null || shares is null)
        {
            return null;
        }

        return likes.Value + comments.Value + shares.Value;
    }

    /// <summary>
    /// Engagement Rate in percent points (13.04 = 13.04%) = Interactions / Reach x 100.
    /// Null when either input is null or Reach &lt;= 0 - never a divide-by-zero,
    /// never a fabricated rate.
    /// </summary>
    public static decimal? EngagementRate(long? interactions, long? reach)
    {
        if (interactions is null || reach is null || reach.Value <= 0)
        {
            return null;
        }

        return Math.Round(interactions.Value * 100m / reach.Value, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>Engagement Rate for a period = Total Interactions / Total Reach x 100 (same guard as the daily one).</summary>
    public static decimal? PeriodEngagementRate(long? totalInteractions, long? totalReach)
        => EngagementRate(totalInteractions, totalReach);

    /// <summary>Current - Previous. Null when either side is unavailable.</summary>
    public static decimal? Difference(decimal? current, decimal? previous)
    {
        if (current is null || previous is null)
        {
            return null;
        }

        return current.Value - previous.Value;
    }

    /// <summary>
    /// Growth = ((Current - Previous) / Previous) x 100, rounded to 4 dp for display
    /// derivation. Null when Previous is null or 0 (no division by zero) or when
    /// Current is unavailable.
    /// </summary>
    public static decimal? GrowthPercentage(decimal? current, decimal? previous)
    {
        if (current is null || previous is null || previous.Value == 0m)
        {
            return null;
        }

        return Math.Round((current.Value - previous.Value) / previous.Value * 100m, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>Net Follower = Followers Gained - Followers Lost. Null when either component is null.</summary>
    public static long? NetFollower(long? gained, long? lost)
    {
        if (gained is null || lost is null)
        {
            return null;
        }

        return gained.Value - lost.Value;
    }

    /// <summary>Share of a part inside a total, in percent points. Null when the total is null/0 or the part is null.</summary>
    public static decimal? CompositionPercentage(decimal? part, decimal? total)
    {
        if (part is null || total is null || total.Value <= 0m)
        {
            return null;
        }

        return Math.Round(part.Value / total.Value * 100m, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Sums one value per calendar day of the period. Days without a value are NOT
    /// counted as zero: covered days are reported separately so a partial sum is
    /// always flagged as Partial instead of being presented as a complete total.
    /// </summary>
    public static DailyMetricAggregate Sum(IEnumerable<long?> dailyValues, int daysInPeriod)
    {
        long? sum = null;
        var covered = 0;

        foreach (var value in dailyValues)
        {
            if (value is null)
            {
                continue;
            }

            sum = (sum ?? 0L) + value.Value;
            covered++;
        }

        return new DailyMetricAggregate(sum, covered, StatusFor(covered, daysInPeriod));
    }

    /// <summary>Completeness status from the number of days that actually carry a value.</summary>
    public static DailyMetricStatus StatusFor(int daysCovered, int daysInPeriod)
    {
        if (daysCovered <= 0)
        {
            return DailyMetricStatus.NoData;
        }

        return daysCovered < daysInPeriod ? DailyMetricStatus.Partial : DailyMetricStatus.Complete;
    }

    /// <summary>The weakest of two statuses (Complete &lt; Partial &lt; NoData &lt; Unavailable).</summary>
    public static DailyMetricStatus Worst(DailyMetricStatus a, DailyMetricStatus b)
        => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(DailyMetricStatus status) => status switch
    {
        DailyMetricStatus.Complete => 0,
        DailyMetricStatus.Partial => 1,
        DailyMetricStatus.NoData => 2,
        _ => 3
    };

    /// <summary>
    /// Whether a donut composition may be rendered. A composition is only honest when
    /// every component covers the whole period and the denominator is positive;
    /// partial or missing inputs render the empty state instead of fake shares.
    /// </summary>
    public static bool CanRenderComposition(IEnumerable<DailyMetricStatus> componentStatuses, decimal? total)
    {
        if (total is null || total.Value <= 0m)
        {
            return false;
        }

        return componentStatuses.All(s => s == DailyMetricStatus.Complete);
    }

    /// <summary>Latest follower snapshot dated on or before the period end (snapshots after EndDate are never used).</summary>
    public static FollowerSnapshot? FollowerSnapshotAtOrBefore(IEnumerable<FollowerSnapshot>? snapshots, DateTime periodEnd)
    {
        if (snapshots is null)
        {
            return null;
        }

        return snapshots
            .Where(s => s.AsOfDate.Date <= periodEnd.Date)
            .OrderByDescending(s => s.AsOfDate)
            .ThenByDescending(s => s.CapturedAt)
            .FirstOrDefault();
    }
}
