using System;
using System.Linq;
using KaleContentOps.Services.CreativePerformance;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// Business rules for the Daily Report date range (spec C.1-C.6):
/// inclusive calendar dates, no snapping, equal-length Previous Period ending
/// exactly one day before StartDate, month/year/leap-year boundaries.
/// </summary>
public class DailyReportDateRangeTests
{
    [Fact]
    public void Create_Strips_Time_Component()
    {
        var range = DailyReportDateRange.Create(
            new DateTime(2026, 8, 23, 15, 42, 10),
            new DateTime(2026, 9, 5, 8, 1, 3));

        Assert.Equal(new DateTime(2026, 8, 23), range.Start);
        Assert.Equal(new DateTime(2026, 9, 5), range.End);
    }

    [Fact]
    public void Create_SingleDayRange_IsValid()
    {
        var range = DailyReportDateRange.Create(new DateTime(2026, 8, 23), new DateTime(2026, 8, 23));

        Assert.Equal(1, range.DayCount);
        Assert.Equal(new DateTime(2026, 8, 24), range.EndExclusive);
    }

    [Fact]
    public void Create_EndBeforeStart_Throws_NeverSilentlySwaps()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DailyReportDateRange.Create(new DateTime(2026, 9, 5), new DateTime(2026, 8, 23)));

        Assert.Contains("EndDate", ex.Message);
    }

    [Fact]
    public void Validate_ReturnsError_InsteadOfThrowing()
    {
        Assert.NotNull(DailyReportDateRange.Validate(new DateTime(2026, 9, 5), new DateTime(2026, 8, 23)));
        Assert.Null(DailyReportDateRange.Validate(new DateTime(2026, 8, 23), new DateTime(2026, 9, 5)));
    }

    [Fact]
    public void Create_RangeAboveSafetyLimit_Throws()
    {
        var start = new DateTime(2026, 1, 1);
        Assert.Throws<ArgumentException>(() =>
            DailyReportDateRange.Create(start, start.AddDays(DailyReportDateRange.MaxDayCount)));
    }

    [Fact]
    public void Previous_SpecExample_23AugTo5Sep2026_MapsTo_9To22Aug2026()
    {
        // Spec C.2 example (explicit in the user requirement):
        // Current 23 Agt - 5 Sep 2026 (14 days) -> Previous 9 - 22 Agt 2026 (14 days).
        var current = DailyReportDateRange.Create(new DateTime(2026, 8, 23), new DateTime(2026, 9, 5));
        var previous = current.Previous();

        Assert.Equal(new DateTime(2026, 8, 9), previous.Start);
        Assert.Equal(new DateTime(2026, 8, 22), previous.End);
        Assert.Equal(current.DayCount, previous.DayCount);
    }

    [Fact]
    public void Previous_IsAlwaysSameLength_AndNeverOverlaps()
    {
        DateTime[] starts =
        {
            new(2026, 1, 1), new(2026, 2, 28), new(2024, 2, 1), new(2026, 12, 31),
            new(2025, 3, 14), new(2026, 6, 30)
        };

        foreach (var start in starts)
        {
            var current = DailyReportDateRange.Create(start, start.AddDays(13));
            var previous = current.Previous();

            Assert.Equal(14, previous.DayCount);
            Assert.Equal(current.Start.AddDays(-1), previous.End);
            Assert.True(previous.End < current.Start, $"{previous} must end before {current} starts");
            Assert.Equal(previous.EndExclusive, current.Start);
        }
    }

    [Fact]
    public void Previous_CrossesYearBoundary()
    {
        var current = DailyReportDateRange.Create(new DateTime(2026, 1, 1), new DateTime(2026, 1, 14));
        var previous = current.Previous();

        Assert.Equal(new DateTime(2025, 12, 18), previous.Start);
        Assert.Equal(new DateTime(2025, 12, 31), previous.End);
    }

    [Fact]
    public void Previous_LeapYear_February()
    {
        // 2024 is a leap year: Feb has 29 days.
        var current = DailyReportDateRange.Create(new DateTime(2024, 2, 1), new DateTime(2024, 2, 29));
        Assert.Equal(29, current.DayCount);

        var previous = current.Previous();
        Assert.Equal(new DateTime(2024, 1, 3), previous.Start);
        Assert.Equal(new DateTime(2024, 1, 31), previous.End);
        Assert.Equal(29, previous.DayCount);
    }

    [Fact]
    public void Previous_NonLeapYear_February()
    {
        // 2026 is NOT a leap year: Feb has 28 days.
        var current = DailyReportDateRange.Create(new DateTime(2026, 2, 1), new DateTime(2026, 2, 28));
        Assert.Equal(28, current.DayCount);

        var previous = current.Previous();
        Assert.Equal(new DateTime(2026, 1, 4), previous.Start);
        Assert.Equal(new DateTime(2026, 1, 31), previous.End);
    }

    [Fact]
    public void Days_YieldsEveryCalendarDate_Ascending_ExactlyOnce()
    {
        var range = DailyReportDateRange.Create(new DateTime(2026, 8, 23), new DateTime(2026, 9, 5));
        var days = new System.Collections.Generic.List<DateTime>(range.Days());

        Assert.Equal(14, days.Count);
        Assert.Equal(range.Start, days[0]);
        Assert.Equal(range.End, days[^1]);
        for (var i = 1; i < days.Count; i++)
        {
            Assert.Equal(days[i - 1].AddDays(1), days[i]);
        }
        Assert.Equal(days.Count, days.Distinct().Count());
    }

    [Fact]
    public void NoAutoSnap_SelectingMidWeekRange_KeepsExactDates()
    {
        // 2026-08-26 is a Wednesday; neither bound may be moved to a week boundary.
        var range = DailyReportDateRange.Create(new DateTime(2026, 8, 26), new DateTime(2026, 9, 2));

        Assert.Equal(new DateTime(2026, 8, 26), range.Start);
        Assert.Equal(new DateTime(2026, 9, 2), range.End);
    }
}
