using System;
using System.Collections.Generic;

namespace KaleContentOps.Services.CreativePerformance;

// ============================================================================
// Creative Performance - Monthly Report: data contract.
// Same honesty contract as the Daily and Weekly Reports (pure transport, nullable
// numerics only, date-only DateTimes, explicit status per metric so the UI can
// render an honest empty/partial state instead of a fabricated zero).
// 
// Month-shaped data lives in the types below instead of forcing DailyReportRow
// or WeeklyReportRow into that role.
// ============================================================================

/// <summary>Requested period for the Monthly Report (inclusive calendar dates, Asia/Jakarta).</summary>
public sealed class MonthlyReportFilter
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}

/// <summary>Completeness of ONE monthly bucket's value. Same four states as
/// <see cref="DailyMetricStatus"/>; kept as that enum so cards, tables and
/// notices share one vocabulary across all three reports.</summary>
public sealed class MonthlyMetricState
{
    public DailyMetricStatus Status { get; init; } = DailyMetricStatus.Unavailable;

    /// <summary>Calendar days of the month that actually carry a value (0..DayCount).</summary>
    public int DaysCovered { get; init; }

    /// <summary>Total calendar days in this month bucket (the clipped DayCount).</summary>
    public int DaysInMonth { get; init; }

    /// <summary>Why the value is missing (null when Complete/Partial with a value).</summary>
    public string? Reason { get; init; }
}

/// <summary>One calendar month of the Monthly Report. Used by BOTH the monthly detail table
/// (descending) and the charts (ascending) so every surface renders literally the same month values.
/// Null means "no value" - never 0.</summary>
public sealed class MonthlyReportRow
{
    /// <summary>Month start (inclusive, 1st of month at the earliest).</summary>
    public DateTime Start { get; init; }

    /// <summary>Month end (inclusive, last day of month at the latest).</summary>
    public DateTime End { get; init; }

    /// <summary>Total calendar days in this month bucket (1..31).</summary>
    public int DayCount { get; init; }

    /// <summary>True when this month is not a full calendar month.</summary>
    public bool IsPartial { get; init; }

    public long? Reach { get; init; }

    public long? VideoViews { get; init; }

    public long? ProfileViews { get; init; }

    public long? Interactions { get; init; }

    /// <summary>Engagement Rate of the month in percent points. Always null while Reach is unavailable.</summary>
    public decimal? EngagementRate { get; init; }

    public long? NetFollower { get; init; }

    /// <summary>Per-metric status for this month (keyed by <see cref="DailyReportMetrics"/> keys).</summary>
    public IReadOnlyDictionary<string, MonthlyMetricState> MetricStates { get; init; }
        = new Dictionary<string, MonthlyMetricState>();

    /// <summary>True when at least one metric carries a value.</summary>
    public bool HasAnyValue => Reach.HasValue || VideoViews.HasValue || ProfileViews.HasValue
        || Interactions.HasValue || EngagementRate.HasValue || NetFollower.HasValue;
}

/// <summary>Complete Monthly Report payload for one selected range.</summary>
public sealed class MonthlyReportData
{
    // ---- Period info (all dates Asia/Jakarta, inclusive) ----
    public DateTime CurrentStart { get; init; }
    public DateTime CurrentEnd { get; init; }
    public DateTime PreviousStart { get; init; }
    public DateTime PreviousEnd { get; init; }

    public int CurrentDayCount { get; init; }
    public int PreviousDayCount { get; init; }

    /// <summary>Number of monthly buckets (months) in the selected period.</summary>
    public int CurrentBucketCount { get; init; }

    /// <summary>Number of monthly buckets in the comparison period.</summary>
    public int PreviousBucketCount { get; init; }

    public string TimeZoneId { get; init; } = "Asia/Jakarta";
    public string TimeZoneLabel { get; init; } = "GMT+7";

    /// <summary>7 summary cards in mockup order (Reach ... Total Follower).</summary>
    public IReadOnlyList<DailyReportMetric> Cards { get; init; } = Array.Empty<DailyReportMetric>();

    /// <summary>Comparison-table rows (the 6 metrics with a delta; Total Follower excluded).</summary>
    public IReadOnlyList<DailyReportMetric> ComparisonRows { get; init; } = Array.Empty<DailyReportMetric>();

    /// <summary>Chart series: the period's monthly buckets, ASCENDING (oldest first).</summary>
    public IReadOnlyList<MonthlyReportRow> Trend { get; init; } = Array.Empty<MonthlyReportRow>();

    /// <summary>Monthly detail table: the same row objects, DESCENDING (newest first).</summary>
    public IReadOnlyList<MonthlyReportRow> MonthlyRows { get; init; } = Array.Empty<MonthlyReportRow>();

    public DailyReportComposition InteractionComposition { get; init; } = new();

    public DailyReportComposition ViewerComposition { get; init; } = new();

    /// <summary>Page-level warnings (unavailable source / partial coverage / reconciliation).</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>Footer: per-metric definition + source status.</summary>
    public IReadOnlyList<DailyReportSourceNote> SourceNotes { get; init; }
        = Array.Empty<DailyReportSourceNote>();

    /// <summary>Engagement Rate of the overall period in percent points.</summary>
    public decimal? EngagementRate { get; init; }

    /// <summary>Total follower count at the end of the current period.</summary>
    public long? TotalFollower { get; init; }

    /// <summary>False when no metric has any value - the page shows empty states everywhere.</summary>
    public bool HasAnyMetricData { get; init; }
}