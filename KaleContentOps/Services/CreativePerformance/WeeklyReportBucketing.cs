using System;
using System.Collections.Generic;

namespace KaleContentOps.Services.CreativePerformance;

// ============================================================================
// Creative Performance - Weekly Report: Monday-Sunday bucketing.
//
// Business rules (Weekly Report spec, approved decision A):
//  - A week runs MONDAY through SUNDAY in the shop reporting calendar
//    (Asia/Jakarta - all values are already date-only midnight DateTimes,
//    matching DailyReportDateRange).
//  - The user-selected range is NEVER snapped or shifted: every bucket is
//    CLIPPED to the selected [Start, End] range, so a selection that starts or
//    ends midweek produces a shorter first/last bucket.
//  - One bucket per intersecting calendar week, in ascending order, including
//    single-day ranges.
//  - DayCount = number of calendar days actually included (clipped), and
//    IsPartial = DayCount < 7 (a full Monday-Sunday week).
//
// Example (spec): 2026-08-23 (Sunday) .. 2026-09-05 produces
//    2026-08-23..2026-08-23 (1 day, partial),
//    2026-08-24..2026-08-30 (7 days, full),
//    2026-08-31..2026-09-05 (6 days, partial).
// ============================================================================

/// <summary>One calendar week (Monday-Sunday) clipped to the selected report range.</summary>
public sealed class WeeklyBucket
{
    /// <summary>Inclusive first day of the bucket (max(Monday of week, range start)).</summary>
    public DateTime Start { get; init; }

    /// <summary>Inclusive last day of the bucket (min(Sunday of week, range end)).</summary>
    public DateTime End { get; init; }

    /// <summary>Number of calendar days included in this bucket (inclusive): 1..7.</summary>
    public int DayCount => (End - Start).Days + 1;

    /// <summary>True when the bucket does not cover a full Monday-Sunday week.</summary>
    public bool IsPartial => DayCount < FullWeekDays;

    /// <summary>Exclusive upper bound for the half-open provider window: End + 1 day.</summary>
    public DateTime EndExclusive => End.AddDays(1);

    /// <summary>Length of an un-clipped Monday-Sunday week.</summary>
    public const int FullWeekDays = 7;

    public override string ToString() => $"{Start:yyyy-MM-dd}..{End:yyyy-MM-dd} ({DayCount}d{(IsPartial ? ", partial" : string.Empty)})";
}

public static class WeeklyReportBucketing
{
    /// <summary>
    /// The MONDAY of the calendar week containing <paramref name="date"/>.
    /// Sunday (DayOfWeek = 0) belongs to the week that started on the previous Monday.
    /// </summary>
    public static DateTime MondayOf(DateTime date)
    {
        var day = date.Date;
        var offsetToMonday = ((int)day.DayOfWeek + 6) % 7; // Mon=0 .. Sat=5, Sun=6
        return day.AddDays(-offsetToMonday);
    }

    /// <summary>The Sunday of the calendar week containing <paramref name="date"/>.</summary>
    public static DateTime SundayOf(DateTime date) => MondayOf(date).AddDays(6);

    /// <summary>
    /// Every Monday-Sunday week intersecting the validated range, clipped to it,
    /// in ascending order. The range is never extended beyond its own bounds.
    /// </summary>
    public static IReadOnlyList<WeeklyBucket> Buckets(DailyReportDateRange range)
    {
        var buckets = new List<WeeklyBucket>();

        for (var start = range.Start; start <= range.End;)
        {
            var weekEnd = SundayOf(start);
            var end = weekEnd < range.End ? weekEnd : range.End;

            buckets.Add(new WeeklyBucket { Start = start, End = end });

            start = end.AddDays(1);
        }

        return buckets;
    }

    /// <summary>Convenience overload: validated inclusive range from raw dates.</summary>
    public static IReadOnlyList<WeeklyBucket> Buckets(DateTime start, DateTime end)
        => Buckets(DailyReportDateRange.Create(start, end));
}
