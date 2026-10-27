using System;
using System.Globalization;

namespace KaleContentOps.Services.CreativePerformance;

// ============================================================================
// Immutable, validated calendar range for the Creative Performance - Monthly Report.
// Same semantics as the Daily Report (inclusive bounds, Asia/Jakarta, never snapped)
// but with month-level grouping instead of day-level.
// ============================================================================

public readonly struct MonthlyReportDateRange : IEquatable<MonthlyReportDateRange>
{
    /// <summary>Inclusive first day of the range.</summary>
    public DateTime Start { get; }

    /// <summary>Inclusive last day of the range.</summary>
    public DateTime End { get; }

    /// <summary>Exclusive upper bound for database filters: End + 1 month.</summary>
    public DateTime EndExclusive => End.AddMonths(1);

    /// <summary>Number of calendar days covered (inclusive): at least 1.</summary>
    public int DayCount => (End - Start).Days + 1;

    public MonthlyReportDateRange(DateTime start, DateTime end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Validates an inclusive range. Time components are stripped (both bounds are calendar dates).</summary>
    public static MonthlyReportDateRange Create(DateTime start, DateTime end)
        => Create(start, end, nameof(start), nameof(end));

    /// <summary>Same as Create(DateTime, DateTime) with caller-chosen parameter names (validation messages).</summary>
    public static MonthlyReportDateRange Create(DateTime start, DateTime end, string startName, string endName)
    {
        var s = start.Date;
        var e = end.Date;

        if (e < s)
        {
            throw new ArgumentException(
                "EndDate must not be earlier than StartDate.",
                endName);
        }

        var days = (e - s).Days + 1;
        if (days > MaxDayCount)
        {
            throw new ArgumentException(
                $"Date range is too large ({days} days). Maximum supported range is {MaxDayCount} days.",
                endName);
        }

        return new MonthlyReportDateRange(s, e);
    }

    /// <summary>Non-throwing validation used by the controller: returns an error message or null when valid.</summary>
    public static string? Validate(DateTime start, DateTime end)
    {
        try
        {
            Create(start, end);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Technical safety limit (10 years in days). Prevents a huge range from allocating an unbounded month series.</summary>
    public const int MaxDayCount = 3660;

    /// <summary>
    /// The apples-to-apples Previous Period: the same DayCount calendar days ending
    /// exactly one day before this range's Start. Both periods never overlap.
    /// </summary>
    /// <example>
    /// Example (spec C.2): 2026-04-09..2026-09-05 (150 days) ->
    /// 2025-11-10..2026-04-08 (150 days).
    /// </example>
    public MonthlyReportDateRange Previous()
    {
        var previousEnd = Start.AddDays(-1);
        var previousStart = previousEnd.AddDays(-(DayCount - 1));
        return MonthlyReportDateRange.Create(previousStart, previousEnd);
    }

    /// <summary>Every calendar month that intersects the range, in ascending order.</summary>
    /// <remarks>
    /// A month "intersects" when any of its days fall within the range.
    /// The first and last months may be partial (clipped to the range boundaries).
    /// </remarks>
    public IEnumerable<MonthlyBucket> Months()
    {
        var current = Start;
        while (current <= End)
        {
            var monthStart = new DateTime(current.Year, current.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var clippedEnd = Math.Min(monthEnd, End);
            var dayCount = (clippedEnd - monthStart).Days + 1;
            var isPartial = monthStart < Start || clippedEnd < End;

            yield return new MonthlyBucket
            {
                Start = monthStart,
                End = clippedEnd,
                DayCount = dayCount,
                IsPartial = isPartial
            };


            current = monthEnd.AddDays(1);
        }
    }

    /// <summary>Half-open [Start, EndExclusive) bounds for sargable database filters.</summary>
    public (DateTime StartInclusive, DateTime EndExclusive) ToBounds() => (Start, EndExclusive);

    public bool Equals(MonthlyReportDateRange other) => Start == other.Start && End == other.End;

    public override bool Equals(object? obj) => obj is MonthlyReportDateRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Start, End);

    public override string ToString() => $"{{Start: {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}}}";
}

// Helper record for month bucket yields from MonthlyReportDateRange.Months()
public readonly struct MonthlyBucket
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int DayCount { get; init; }
    public bool IsPartial { get; init; }

    public MonthlyBucket(DateTime start, DateTime end, int dayCount, bool isPartial)
    {
        Start = start;
        End = end;
        DayCount = dayCount;
        IsPartial = isPartial;
    }
}