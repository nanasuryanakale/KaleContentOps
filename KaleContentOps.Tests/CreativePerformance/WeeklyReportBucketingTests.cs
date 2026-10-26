using System;
using System.Linq;
using KaleContentOps.Services.CreativePerformance;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// Business rules for the Weekly Report bucketing (approved decision A):
/// Monday-Sunday weeks in the shop calendar, NEVER snapped - every bucket is
/// clipped to the selected range, with honest DayCount / IsPartial flags.
/// </summary>
public class WeeklyReportBucketingTests
{
    [Fact]
    public void MondayOf_ReturnsMonday_ForEveryWeekday()
    {
        // 2026-08-24 is a Monday; every day of that week maps back to it.
        for (var offset = 0; offset <= 6; offset++)
        {
            Assert.Equal(new DateTime(2026, 8, 24), WeeklyReportBucketing.MondayOf(new DateTime(2026, 8, 24).AddDays(offset)));
        }
    }

    [Fact]
    public void SundayOf_IsSixDaysAfterMondayOf()
    {
        Assert.Equal(new DateTime(2026, 8, 30), WeeklyReportBucketing.SundayOf(new DateTime(2026, 8, 26)));
    }

    [Fact]
    public void Buckets_SpecExample_23AugTo5Sep_Produces_1_7_6_DayBuckets()
    {
        var buckets = WeeklyReportBucketing.Buckets(new DateTime(2026, 8, 23), new DateTime(2026, 9, 5));

        Assert.Equal(3, buckets.Count);

        // 2026-08-23 is a Sunday: a clipped single-day bucket.
        Assert.Equal(new DateTime(2026, 8, 23), buckets[0].Start);
        Assert.Equal(new DateTime(2026, 8, 23), buckets[0].End);
        Assert.Equal(1, buckets[0].DayCount);
        Assert.True(buckets[0].IsPartial);

        // 2026-08-24 (Mon) .. 2026-08-30 (Sun): a full week.
        Assert.Equal(new DateTime(2026, 8, 24), buckets[1].Start);
        Assert.Equal(new DateTime(2026, 8, 30), buckets[1].End);
        Assert.Equal(7, buckets[1].DayCount);
        Assert.False(buckets[1].IsPartial);

        // 2026-08-31 (Mon) .. 2026-09-05 (Sat): clipped at the range end.
        Assert.Equal(new DateTime(2026, 8, 31), buckets[2].Start);
        Assert.Equal(new DateTime(2026, 9, 5), buckets[2].End);
        Assert.Equal(6, buckets[2].DayCount);
        Assert.True(buckets[2].IsPartial);
    }

    [Fact]
    public void Buckets_FullWeek_SingleBucket_NotPartial()
    {
        var buckets = WeeklyReportBucketing.Buckets(new DateTime(2026, 8, 24), new DateTime(2026, 8, 30));

        var bucket = Assert.Single(buckets);
        Assert.Equal(7, bucket.DayCount);
        Assert.False(bucket.IsPartial);
    }

    [Fact]
    public void Buckets_SingleDayMonday_And_SingleDaySunday_AreBothPartial()
    {
        var monday = Assert.Single(WeeklyReportBucketing.Buckets(new DateTime(2026, 8, 24), new DateTime(2026, 8, 24)));
        Assert.Equal(1, monday.DayCount);
        Assert.True(monday.IsPartial);

        var sunday = Assert.Single(WeeklyReportBucketing.Buckets(new DateTime(2026, 8, 23), new DateTime(2026, 8, 23)));
        Assert.Equal(1, sunday.DayCount);
        Assert.True(sunday.IsPartial);
    }

    [Fact]
    public void Buckets_MidweekStartAndEnd_ClipBothEdges()
    {
        // Wednesday .. Tuesday: first bucket clipped at the start, last at the end.
        var buckets = WeeklyReportBucketing.Buckets(new DateTime(2026, 8, 26), new DateTime(2026, 9, 1));

        Assert.Equal(2, buckets.Count);
        Assert.Equal(new DateTime(2026, 8, 26), buckets[0].Start); // Wed
        Assert.Equal(new DateTime(2026, 8, 30), buckets[0].End);   // Sun
        Assert.Equal(5, buckets[0].DayCount);
        Assert.Equal(new DateTime(2026, 8, 31), buckets[1].Start); // Mon
        Assert.Equal(new DateTime(2026, 9, 1), buckets[1].End);    // Tue
        Assert.Equal(2, buckets[1].DayCount);
    }

    [Fact]
    public void Buckets_MonthBoundary_ClipsWithoutSnapping()
    {
        // 2026-08-30 (Sun) .. 2026-09-06 (Sun).
        var buckets = WeeklyReportBucketing.Buckets(new DateTime(2026, 8, 30), new DateTime(2026, 9, 6));

        Assert.Equal(2, buckets.Count);
        Assert.Equal(new DateTime(2026, 8, 30), buckets[0].Start);
        Assert.Equal(new DateTime(2026, 8, 30), buckets[0].End);
        Assert.Equal(1, buckets[0].DayCount);

        Assert.Equal(new DateTime(2026, 8, 31), buckets[1].Start);
        Assert.Equal(new DateTime(2026, 9, 6), buckets[1].End);
        Assert.Equal(7, buckets[1].DayCount);
        Assert.False(buckets[1].IsPartial);
    }

    [Fact]
    public void Buckets_YearBoundary_KeepsOneFullWeek()
    {
        // 2026-12-28 (Mon) .. 2027-01-03 (Sun): one full week crossing the year end.
        var buckets = WeeklyReportBucketing.Buckets(new DateTime(2026, 12, 28), new DateTime(2027, 1, 3));

        var bucket = Assert.Single(buckets);
        Assert.Equal(new DateTime(2026, 12, 28), bucket.Start);
        Assert.Equal(new DateTime(2027, 1, 3), bucket.End);
        Assert.Equal(7, bucket.DayCount);
        Assert.False(bucket.IsPartial);
    }

    [Fact]
    public void Buckets_LeapYear_Includes29February()
    {
        // 2024-02-27 (Tue) .. 2024-03-03 (Sun) spans leap day 2024-02-29.
        var buckets = WeeklyReportBucketing.Buckets(new DateTime(2024, 2, 27), new DateTime(2024, 3, 3));

        var bucket = Assert.Single(buckets);
        Assert.Equal(new DateTime(2024, 2, 27), bucket.Start);
        Assert.Equal(new DateTime(2024, 3, 3), bucket.End);
        Assert.Equal(6, bucket.DayCount);
        Assert.True(bucket.IsPartial);
    }

    [Fact]
    public void Buckets_CoverEveryDayExactlyOnce_NeverMoreThanSevenDays()
    {
        var start = new DateTime(2026, 8, 23);
        var end = new DateTime(2026, 9, 5);
        var buckets = WeeklyReportBucketing.Buckets(start, end);

        Assert.Equal(14, buckets.Sum(b => b.DayCount));
        Assert.All(buckets, b => Assert.InRange(b.DayCount, 1, WeeklyBucket.FullWeekDays));

        // Contiguous, ascending, inside the range, never snapped beyond it.
        for (var i = 0; i < buckets.Count; i++)
        {
            Assert.InRange(buckets[i].Start, start, end);
            Assert.InRange(buckets[i].End, buckets[i].Start, end);
            if (i > 0)
            {
                Assert.Equal(buckets[i - 1].End.AddDays(1), buckets[i].Start);
            }
        }
    }

    [Fact]
    public void EndExclusive_IsOneDayAfterInclusiveEnd()
    {
        var buckets = WeeklyReportBucketing.Buckets(new DateTime(2026, 8, 31), new DateTime(2026, 9, 5));

        var bucket = Assert.Single(buckets);
        Assert.Equal(new DateTime(2026, 9, 6), bucket.EndExclusive);
    }

    [Fact]
    public void Buckets_EndBeforeStart_Throws_NeverSilentlySwaps()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WeeklyReportBucketing.Buckets(new DateTime(2026, 9, 5), new DateTime(2026, 8, 23)));

        Assert.Contains("EndDate", ex.Message);
    }
}
