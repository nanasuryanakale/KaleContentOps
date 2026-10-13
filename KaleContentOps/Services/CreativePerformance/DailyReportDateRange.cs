using System;

namespace KaleContentOps.Services.CreativePerformance;

/// <summary>
/// Immutable, validated calendar range for the Creative Performance - Daily Report.
///
/// Business rules implemented here (Daily Report spec, sections C.1-C.6):
///  - Both bounds are INCLUSIVE calendar dates in the shop reporting timezone (Asia/Jakarta).
///  - The user-selected dates are NEVER shifted, snapped or rolled (no week/month boundaries).
///  - Previous Period = the same number of calendar days, ending exactly one day before
///    the Current Period StartDate (apples-to-apples, never overlapping).
///  - EndDate earlier than StartDate is rejected (ArgumentException), never silently swapped.
///
/// All values are midnight DateTime (date-only semantics), matching the existing
/// Daily Summary / Winning Content convention in this codebase.
/// </summary>
public readonly struct DailyReportDateRange : IEquatable<DailyReportDateRange>
{
    /// <summary>Inclusive first day of the range.</summary>
    public DateTime Start { get; }

    /// <summary>Inclusive last day of the range.</summary>
    public DateTime End { get; }

    /// <summary>Exclusive upper bound for database filters: End + 1 day.</summary>
    public DateTime EndExclusive => End.AddDays(1);

    /// <summary>Number of calendar days covered (inclusive): at least 1.</summary>
    public int DayCount => (End - Start).Days + 1;

    private DailyReportDateRange(DateTime start, DateTime end)
    {
        Start = start;
        End = end;
    }

    /// <summary>
    /// Validates an inclusive range. Time components are stripped (both bounds are
    /// calendar dates). Throws ArgumentException when End is before Start or when the
    /// range exceeds the technical safety limit of 3660 days (10 years) - a request
    /// guard, not a business rule.
    /// </summary>
    public static DailyReportDateRange Create(DateTime start, DateTime end)
        => Create(start, end, nameof(start), nameof(end));

    /// <summary>Same as <see cref="Create(DateTime, DateTime)"/> with caller-chosen parameter names (validation messages).</summary>
    public static DailyReportDateRange Create(DateTime start, DateTime end, string startName, string endName)
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

        return new DailyReportDateRange(s, e);
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

    /// <summary>Technical safety limit (10 years). Prevents a huge range from allocating an unbounded day series.</summary>
    public const int MaxDayCount = 3660;

    /// <summary>
    /// The apples-to-apples Previous Period: the same DayCount calendar days ending
    /// exactly one day before this range's Start. Both periods never overlap.
    ///
    /// Example (spec C.2): 2026-08-23..2026-09-05 (14 days) -> 2026-08-09..2026-08-22 (14 days).
    /// </summary>
    public DailyReportDateRange Previous()
    {
        var previousEnd = Start.AddDays(-1);
        var previousStart = previousEnd.AddDays(-(DayCount - 1));
        return new DailyReportDateRange(previousStart, previousEnd);
    }

    /// <summary>Every calendar day of the range in ascending order (start to end inclusive).</summary>
    public IEnumerable<DateTime> Days()
    {
        for (var day = Start; day <= End; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    /// <summary>Half-open [Start, EndExclusive) bounds for sargable database filters.</summary>
    public (DateTime StartInclusive, DateTime EndExclusive) ToBounds() => (Start, EndExclusive);

    public bool Equals(DailyReportDateRange other) => Start == other.Start && End == other.End;

    public override bool Equals(object? obj) => obj is DailyReportDateRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Start, End);

    public override string ToString() => $"{Start:yyyy-MM-dd}..{End:yyyy-MM-dd}";
}
