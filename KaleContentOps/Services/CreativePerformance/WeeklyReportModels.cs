using System;
using System.Collections.Generic;

namespace KaleContentOps.Services.CreativePerformance;

// ============================================================================
// Creative Performance - Weekly Report: data contract.
//
// Same honesty contract as the Daily Report (pure transport, nullable numerics
// only, date-only DateTimes, explicit status per metric so the UI can render an
// honest empty/partial state instead of a fabricated zero).
//
// Reused from the Daily contract because their semantics are granularity-neutral:
//  - DailyReportMetric        (cards + comparison rows: no date shape)
//  - DailyMetricStatus / DailyMetricDeltaKind
//  - DailyReportComposition   (donut payloads)
//  - DailyReportSourceNote    (footer notes)
//  - DailyReportMetrics       (canonical metric keys)
// Week-shaped data (bucket dates, day counts, partial flags) lives in the
// weekly-specific types below instead of forcing DailyReportRow into that role.
// ============================================================================

/// <summary>Requested period for the Weekly Report (inclusive calendar dates, Asia/Jakarta).</summary>
public sealed class WeeklyReportFilter
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}

/// <summary>
/// Completeness of ONE weekly bucket's value. Same four states as
/// <see cref="DailyMetricStatus"/>; kept as that enum so cards, tables and
/// notices share one vocabulary across both reports.
/// </summary>
public sealed class WeeklyMetricState
{
    public DailyMetricStatus Status { get; init; } = DailyMetricStatus.Unavailable;

    /// <summary>Calendar days of the bucket that actually carry a value (0..DayCount).</summary>
    public int DaysCovered { get; init; }

    /// <summary>Total calendar days of this bucket (the clipped DayCount).</summary>
    public int DaysInBucket { get; init; }

    /// <summary>Why the value is missing (null when Complete/Partial with a value).</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// One calendar week of the Weekly Report. Used by BOTH the weekly detail table
/// (descending) and the charts (ascending) so every surface renders literally the
/// same bucket values. Null means "no value" - never 0.
/// </summary>
public sealed class WeeklyReportRow
{
    /// <summary>Clipped bucket start (inclusive, Monday at the earliest).</summary>
    public DateTime Start { get; init; }

    /// <summary>Clipped bucket end (inclusive, Sunday at the latest).</summary>
    public DateTime End { get; init; }

    /// <summary>Calendar days included in this bucket (1..7).</summary>
    public int DayCount { get; init; }

    /// <summary>True when this bucket is not a full Monday-Sunday week.</summary>
    public bool IsPartial { get; init; }

    public long? Reach { get; init; }

    public long? VideoViews { get; init; }

    public long? ProfileViews { get; init; }

    public long? Interactions { get; init; }

    /// <summary>Engagement Rate of the week in percent points. Always null while Reach is unavailable.</summary>
    public decimal? EngagementRate { get; init; }

    public long? NetFollower { get; init; }

    /// <summary>Per-metric status for this bucket (keyed by <see cref="DailyReportMetrics"/> keys).</summary>
    public IReadOnlyDictionary<string, WeeklyMetricState> MetricStates { get; init; }
        = new Dictionary<string, WeeklyMetricState>();

    /// <summary>
    /// True when at least one metric carries a value - charts keep drawing and the
    /// table shows the row even when most columns are honestly empty.
    /// </summary>
    public bool HasAnyValue => Reach.HasValue || VideoViews.HasValue || ProfileViews.HasValue
        || Interactions.HasValue || EngagementRate.HasValue || NetFollower.HasValue;
}

/// <summary>Complete Weekly Report payload for one selected range.</summary>
public sealed class WeeklyReportData
{
    // ---- Period info (all dates Asia/Jakarta, inclusive) ----
    public DateTime CurrentStart { get; init; }
    public DateTime CurrentEnd { get; init; }
    public DateTime PreviousStart { get; init; }
    public DateTime PreviousEnd { get; init; }

    public int CurrentDayCount { get; init; }
    public int PreviousDayCount { get; init; }

    /// <summary>Number of weekly buckets in the selected period.</summary>
    public int CurrentBucketCount { get; init; }

    /// <summary>Number of weekly buckets in the comparison period.</summary>
    public int PreviousBucketCount { get; init; }

    public string TimeZoneId { get; init; } = "Asia/Jakarta";
    public string TimeZoneLabel { get; init; } = "GMT+7";

    /// <summary>7 summary cards in mockup order (Reach ... Total Follower).</summary>
    public IReadOnlyList<DailyReportMetric> Cards { get; init; } = Array.Empty<DailyReportMetric>();

    /// <summary>Comparison-table rows (the 6 metrics with a delta; Total Follower excluded).</summary>
    public IReadOnlyList<DailyReportMetric> ComparisonRows { get; init; } = Array.Empty<DailyReportMetric>();

    /// <summary>Chart series: the period's weekly buckets, ASCENDING (oldest first).</summary>
    public IReadOnlyList<WeeklyReportRow> Trend { get; init; } = Array.Empty<WeeklyReportRow>();

    /// <summary>Weekly detail table: the same row objects, DESCENDING (newest first).</summary>
    public IReadOnlyList<WeeklyReportRow> WeeklyRows { get; init; } = Array.Empty<WeeklyReportRow>();

    public DailyReportComposition InteractionComposition { get; init; } = new();

    public DailyReportComposition ViewerComposition { get; init; } = new();

    /// <summary>Page-level warnings (unavailable source / partial coverage / reconciliation).</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>Footer: per-metric definition + source status.</summary>
    public IReadOnlyList<DailyReportSourceNote> SourceNotes { get; init; }
        = Array.Empty<DailyReportSourceNote>();

    /// <summary>False when no metric has any value - the page shows empty states everywhere.</summary>
    public bool HasAnyMetricData { get; init; }
}
