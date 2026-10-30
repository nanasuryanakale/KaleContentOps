using System;
using System.Linq;
using KaleContentOps.Services.CreativePerformance;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// Business rules for the Monthly Report bucketing (Monthly Report spec, section B):
/// calendar-month buckets CLIPPED to the selected range (never snapped), inclusive
/// bounds, honest DayCount / IsPartial flags, ascending order, and an equal-length
/// Previous Period that never overlaps the current one.
/// </summary>
public class MonthlyReportBucketingTests
{
    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    [Fact]
    public void Create_EndBeforeStart_Throws_NeverSwaps()
    {
        Assert.Throws<ArgumentException>(() =>
            MonthlyReportDateRange.Create(new DateTime(2026, 9, 5), new DateTime(2026, 8, 23)));
    }

    [Fact]
    public void Create_TooLargeRange_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MonthlyReportDateRange.Create(new DateTime(2020, 1, 1), new DateTime(2050, 1, 1)));
    }

    [Fact]
    public void Validate_ReturnsMessage_OrNull()
    {
        Assert.Null(MonthlyReportDateRange.Validate(new DateTime(2026, 8, 23), new DateTime(2026, 9, 5)));
        Assert.NotNull(MonthlyReportDateRange.Validate(new DateTime(2026, 9, 5), new DateTime(2026, 8, 23)));
    }

    [Fact]
    public void Create_StripsTimeComponent_FromBothBounds()
    {
        var range = MonthlyReportDateRange.Create(
            new DateTime(2026, 8, 23, 15, 42, 10),
            new DateTime(2026, 9, 5, 8, 1, 3));

        Assert.Equal(new DateTime(2026, 8, 23), range.Start);
        Assert.Equal(new DateTime(2026, 9, 5), range.End);
        Assert.Equal(14, range.DayCount);
        Assert.Equal(new DateTime(2026, 9, 6), range.EndExclusive);
    }

    // ------------------------------------------------------------------
    // Buckets: full months, partial months, single day, year rollover
    // ------------------------------------------------------------------

    [Fact]
    public void Months_FullCalendarMonth_SingleBucket_NotPartial()
    {
        var buckets = MonthlyReportBucketing.Months(new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));

        var bucket = Assert.Single(buckets);
        Assert.Equal(new DateTime(2026, 8, 1), bucket.Start);
        Assert.Equal(new DateTime(2026, 8, 31), bucket.End);
        Assert.Equal(31, bucket.DayCount);
        Assert.False(bucket.IsPartial);
    }

    [Fact]
    public void Months_TwoFullMonths_BothNotPartial()
    {
        var buckets = MonthlyReportBucketing.Months(new DateTime(2026, 7, 1), new DateTime(2026, 8, 31));

        Assert.Equal(2, buckets.Count);
        Assert.All(buckets, b => Assert.False(b.IsPartial));
        Assert.Equal(31, buckets[0].DayCount);
        Assert.Equal(31, buckets[1].DayCount);
    }

    [Fact]
    public void Months_PartialFirstAndLastMonth_BothClippedAndPartial()
    {
        // 23 Aug (Sun) .. 5 Sep: 9 days of August + 5 days of September = 14.
        var buckets = MonthlyReportBucketing.Months(new DateTime(2026, 8, 23), new DateTime(2026, 9, 5));

        Assert.Equal(2, buckets.Count);

        Assert.Equal(new DateTime(2026, 8, 23), buckets[0].Start); // clipped at the range start, NOT the 1st
        Assert.Equal(new DateTime(2026, 8, 31), buckets[0].End);
        Assert.Equal(9, buckets[0].DayCount);
        Assert.True(buckets[0].IsPartial);

        Assert.Equal(new DateTime(2026, 9, 1), buckets[1].Start);
        Assert.Equal(new DateTime(2026, 9, 5), buckets[1].End);    // clipped at the range end
        Assert.Equal(5, buckets[1].DayCount);
        Assert.True(buckets[1].IsPartial);

        // Bucket day counts must add up exactly to the range length.
        Assert.Equal(14, buckets.Sum(b => b.DayCount));
    }

    [Fact]
    public void Months_BucketStart_NeverBeforeRangeStart()
    {
        // A previous-period range that starts mid-month must not extend back to
        // the 1st: the first bucket is clipped at Start (no out-of-range days).
        var buckets = MonthlyReportBucketing.Months(new DateTime(2026, 8, 9), new DateTime(2026, 8, 22));

        var bucket = Assert.Single(buckets);
        Assert.Equal(new DateTime(2026, 8, 9), bucket.Start);
        Assert.Equal(new DateTime(2026, 8, 22), bucket.End);
        Assert.Equal(14, bucket.DayCount);
        Assert.True(bucket.IsPartial);
    }

    [Fact]
    public void Months_SingleDayRange_SinglePartialBucket()
    {
        var bucket = Assert.Single(MonthlyReportBucketing.Months(new DateTime(2026, 8, 15), new DateTime(2026, 8, 15)));

        Assert.Equal(new DateTime(2026, 8, 15), bucket.Start);
        Assert.Equal(new DateTime(2026, 8, 15), bucket.End);
        Assert.Equal(1, bucket.DayCount);
        Assert.True(bucket.IsPartial);
    }

    [Fact]
    public void Months_YearRollover_SplitsAcrossCalendarYears()
    {
        var buckets = MonthlyReportBucketing.Months(new DateTime(2026, 12, 15), new DateTime(2027, 1, 5));

        Assert.Equal(2, buckets.Count);

        Assert.Equal(new DateTime(2026, 12, 15), buckets[0].Start);
        Assert.Equal(new DateTime(2026, 12, 31), buckets[0].End);
        Assert.Equal(17, buckets[0].DayCount);

        Assert.Equal(new DateTime(2027, 1, 1), buckets[1].Start);
        Assert.Equal(new DateTime(2027, 1, 5), buckets[1].End);
        Assert.Equal(5, buckets[1].DayCount);

        Assert.Equal(22, buckets.Sum(b => b.DayCount));
    }

    [Fact]
    public void Months_AreAscending_AndMatchTheRangeImplementation()
    {
        var start = new DateTime(2026, 6, 10);
        var end = new DateTime(2026, 10, 7);

        var buckets = MonthlyReportBucketing.Months(start, end);
        var range = MonthlyReportDateRange.Create(start, end);

        Assert.Equal(range.Months().ToList(), buckets);
        Assert.Equal(buckets.OrderBy(b => b.Start).ToList(), buckets);
        Assert.Equal(5, buckets.Count);
        Assert.Equal(start, buckets.First().Start);
        Assert.Equal(end, buckets.Last().End);
        Assert.Equal(120, buckets.Sum(b => b.DayCount)); // = range.DayCount
    }

    [Fact]
    public void Months_InvalidRange_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MonthlyReportBucketing.Months(new DateTime(2026, 9, 5), new DateTime(2026, 8, 23)));
    }

    // ------------------------------------------------------------------
    // Previous Period (equal length, never overlapping)
    // ------------------------------------------------------------------

    [Fact]
    public void Previous_SpecExample_150Days_MatchesDocumentedWindow()
    {
        var current = MonthlyReportDateRange.Create(new DateTime(2026, 4, 9), new DateTime(2026, 9, 5));
        var previous = current.Previous();

        Assert.Equal(150, current.DayCount);
        Assert.Equal(new DateTime(2025, 11, 10), previous.Start);
        Assert.Equal(new DateTime(2026, 4, 8), previous.End);
        Assert.Equal(150, previous.DayCount);
    }

    [Fact]
    public void Previous_IsEqualLength_AndNeverOverlaps_AcrossYearRollover()
    {
        var current = MonthlyReportDateRange.Create(new DateTime(2027, 1, 1), new DateTime(2027, 1, 31));
        var previous = current.Previous();

        Assert.Equal(current.DayCount, previous.DayCount);
        Assert.Equal(current.Start.AddDays(-1), previous.End);
        Assert.True(previous.End < current.Start); // no overlap
        Assert.Equal(new DateTime(2026, 12, 1), previous.Start);
        Assert.Equal(new DateTime(2026, 12, 31), previous.End);
    }

    [Fact]
    public void Previous_OfAPreviousPeriod_IsStillEqualLength()
    {
        var current = MonthlyReportDateRange.Create(new DateTime(2026, 8, 23), new DateTime(2026, 9, 5));
        var previous = current.Previous();

        Assert.Equal(14, previous.DayCount);
        Assert.Equal(new DateTime(2026, 8, 9), previous.Start);
        Assert.Equal(new DateTime(2026, 8, 22), previous.End);

        var previousBuckets = previous.Months().ToList();
        var previousBucket = Assert.Single(previousBuckets);
        Assert.Equal(new DateTime(2026, 8, 9), previousBucket.Start); // clipped, never the 1st
        Assert.Equal(14, previousBucket.DayCount);
    }
}
