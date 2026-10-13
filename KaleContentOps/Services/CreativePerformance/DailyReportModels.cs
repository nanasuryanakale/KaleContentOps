using System;
using System.Collections.Generic;

namespace KaleContentOps.Services.CreativePerformance;

// ============================================================================
// Creative Performance - Daily Report: data contract.
//
// Everything in this file is a pure transport:
//  - Nullable numerics only. The presentation string "-" NEVER appears here.
//  - Date-only DateTime values (midnight), matching Daily Summary / Winning Content.
//  - Explicit availability + completeness state per metric so the UI can render an
//    honest empty / partial state instead of a fabricated zero.
// Formatting (id-ID separators, "+"/"-", "%", "pts", "-") happens in the View only.
// ============================================================================

/// <summary>Canonical metric keys shared by cards, comparison table and notes.</summary>
public static class DailyReportMetrics
{
    public const string Reach = "reach";
    public const string VideoViews = "video_views";
    public const string ProfileViews = "profile_views";
    public const string Interactions = "interactions";
    public const string EngagementRate = "engagement_rate";
    public const string NetFollower = "net_follower";
    public const string TotalFollower = "total_follower";
}

/// <summary>Requested period for the Daily Report (inclusive calendar dates, Asia/Jakarta).</summary>
public sealed class DailyReportFilter
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}

/// <summary>
/// How a metric's change versus the Previous Period must be expressed.
///  - Absolute: difference = Current - Previous, growth = (C - P) / P x 100%.
///  - PercentagePoints: difference is already a percentage-point delta (ER); growth is never shown.
///  - PositionOnly: end-of-period position only (Total Follower); no delta at all.
/// </summary>
public enum DailyMetricDeltaKind
{
    Absolute,
    PercentagePoints,
    PositionOnly
}

/// <summary>
/// Completeness of a computed value. Both NoData and Unavailable render "-", but they
/// are distinct states: NoData = source works, nothing recorded; Unavailable = no
/// verified source for this metric (blocked integration).
/// </summary>
public enum DailyMetricStatus
{
    /// <summary>Value covers every calendar day of the period.</summary>
    Complete,

    /// <summary>Value covers only part of the period - must never be presented as a full-period total.</summary>
    Partial,

    /// <summary>Source is available but no usable value exists for the period.</summary>
    NoData,

    /// <summary>No verified source for this metric (integration blocked).</summary>
    Unavailable
}

/// <summary>One summary card / comparison-table row.</summary>
public sealed class DailyReportMetric
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    /// <summary>Current Period value. Null = render "-". ER is expressed in percent points (13.04 = 13.04%).</summary>
    public decimal? CurrentValue { get; init; }

    /// <summary>Previous Period value. Null = render "-".</summary>
    public decimal? PreviousValue { get; init; }

    /// <summary>Current - Previous (ER: percentage points). Null when either side is unavailable.</summary>
    public decimal? Difference { get; init; }

    /// <summary>((Current - Previous) / Previous) x 100. Null when Previous is 0/unknown or delta kind says no growth.</summary>
    public decimal? GrowthPercentage { get; init; }

    public DailyMetricDeltaKind DeltaKind { get; init; } = DailyMetricDeltaKind.Absolute;

    public DailyMetricStatus CurrentStatus { get; init; } = DailyMetricStatus.Unavailable;

    public DailyMetricStatus PreviousStatus { get; init; } = DailyMetricStatus.Unavailable;

    /// <summary>True when CurrentValue can be shown as a real number (Complete or Partial).</summary>
    public bool IsAvailable => CurrentValue.HasValue
        && CurrentStatus is DailyMetricStatus.Complete or DailyMetricStatus.Partial;

    /// <summary>Honest explanation rendered as a tooltip / helper text when not available.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Set when either side is Partial (value is real but does not cover the whole period).</summary>
    public bool IsPartial => CurrentStatus == DailyMetricStatus.Partial
        || PreviousStatus == DailyMetricStatus.Partial;

    /// <summary>Days covered vs days in period for the Current Period (e.g. "11 dari 14 hari").</summary>
    public string? CoverageNote { get; init; }

    /// <summary>Total Follower is excluded from the comparison table (no delta by design).</summary>
    public bool ShowInComparison { get; init; } = true;
}

/// <summary>
/// One day of the Daily Report. Used BOTH by the daily detail table (descending) and by
/// the charts (ascending) so every surface renders literally the same numbers.
/// Null means "no value" - never 0.
/// </summary>
public sealed class DailyReportRow
{
    public DateTime Date { get; init; }

    public long? Reach { get; init; }

    public long? VideoViews { get; init; }

    public long? ProfileViews { get; init; }

    public long? Interactions { get; init; }

    /// <summary>Engagement Rate for this day in percent points (18.44 = 18.44%). Null when Reach or Interactions is missing/zero.</summary>
    public decimal? EngagementRate { get; init; }

    public long? NetFollower { get; init; }

    /// <summary>False when the day has no record at all (all metrics null) - charts must skip it, never plot zero.</summary>
    public bool HasRecord { get; init; }
}

/// <summary>One slice of a donut chart. Percentage is null when it must not be shown.</summary>
public sealed class DailyReportCompositionSegment
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Color { get; init; } = string.Empty;

    public decimal? Value { get; init; }

    /// <summary>0..100. Null when unavailable - the UI renders "-" instead of a fake share.</summary>
    public decimal? Percentage { get; init; }
}

/// <summary>Donut-chart payload (Interactions composition / new vs returning viewers).</summary>
public sealed class DailyReportComposition
{
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }

    public string? UnavailableReason { get; init; }

    /// <summary>Denominator behind the percentages (null when unavailable).</summary>
    public decimal? Total { get; init; }

    public IReadOnlyList<DailyReportCompositionSegment> Segments { get; init; }
        = Array.Empty<DailyReportCompositionSegment>();
}

/// <summary>Footer line: metric definition + data-source status (verified facts only).</summary>
public sealed class DailyReportSourceNote
{
    public string Metric { get; init; } = string.Empty;

    public string Definition { get; init; } = string.Empty;

    /// <summary>Short availability statement, e.g. "belum tersambung" / "tersedia".</summary>
    public string Status { get; init; } = string.Empty;
}

/// <summary>Complete Daily Report payload for one selected range.</summary>
public sealed class DailyReportData
{
    // ---- Period info (all dates Asia/Jakarta, inclusive) ----
    public DateTime CurrentStart { get; init; }
    public DateTime CurrentEnd { get; init; }
    public DateTime PreviousStart { get; init; }
    public DateTime PreviousEnd { get; init; }
    public int CurrentDayCount { get; init; }
    public int PreviousDayCount { get; init; }
    public string TimeZoneId { get; init; } = "Asia/Jakarta";
    public string TimeZoneLabel { get; init; } = "GMT+7";

    /// <summary>7 summary cards in mockup order (Reach ... Total Follower).</summary>
    public IReadOnlyList<DailyReportMetric> Cards { get; init; } = Array.Empty<DailyReportMetric>();

    /// <summary>Comparison-table rows (the 6 metrics with a delta; Total Follower excluded).</summary>
    public IReadOnlyList<DailyReportMetric> ComparisonRows { get; init; } = Array.Empty<DailyReportMetric>();

    /// <summary>Line-chart series: Current Period days, ASCENDING (oldest first).</summary>
    public IReadOnlyList<DailyReportRow> Trend { get; init; } = Array.Empty<DailyReportRow>();

    /// <summary>Daily detail table: same rows, DESCENDING (newest first).</summary>
    public IReadOnlyList<DailyReportRow> DailyRows { get; init; } = Array.Empty<DailyReportRow>();

    public DailyReportComposition InteractionComposition { get; init; } = new();

    public DailyReportComposition ViewerComposition { get; init; } = new();

    /// <summary>Page-level warnings (unavailable source / partial coverage). Rendered as a notice bar.</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>Footer: per-metric definition + source status.</summary>
    public IReadOnlyList<DailyReportSourceNote> SourceNotes { get; init; }
        = Array.Empty<DailyReportSourceNote>();

    /// <summary>False when no metric has any value - the page shows empty states everywhere.</summary>
    public bool HasAnyMetricData { get; init; }
}
