using System;
using System.Collections.Generic;
using System.Linq;

namespace KaleContentOps.Services.CreativePerformance;

// ============================================================================
// Creative Performance - Monthly Report: bucketing logic.
// Same principles as the Weekly Report bucketing (Monday-Sunday clips)
// but at month granularity.
// ============================================================================

/// <summary>Groups the selected date range into calendar-month buckets, clipped to
/// the selected range. Each month bucket starts on the 1st and ends on the last
/// day of that month, but may be partial if the range doesn't start on the 1st
/// or end on the last day of the month.</summary>
public static class MonthlyReportBucketing
{
    /// <summary>Derives the monthly buckets for the given inclusive range.</summary>
    /// <exception cref="ArgumentException">Thrown when start > end.</exception>
    public static IReadOnlyList<MonthlyBucket> Months(DateTime start, DateTime end)
        => MonthlyReportDateRange.Create(start, end).Months().ToList();

    /// <summary>Same as Months(DateTime, DateTime) but works on a validated MonthlyReportDateRange.</summary>
    public static IReadOnlyList<MonthlyBucket> Months(MonthlyReportDateRange range)
        => Months(range.Start, range.End);
}