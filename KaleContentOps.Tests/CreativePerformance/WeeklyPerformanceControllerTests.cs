using System;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Controllers;
using KaleContentOps.Services;
using KaleContentOps.Services.CreativePerformance;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace KaleContentOps.Tests.CreativePerformance;

/// <summary>
/// Controller contract: defaults (last 14 calendar days ending yesterday,
/// shop timezone), exact user dates preserved without snapping, invalid range
/// rejected as 400, and both endpoints sharing one service path.
/// </summary>
public class WeeklyPerformanceControllerTests
{
    private sealed class FakeShopTimeZone : IShopTimeZone
    {
        public string TimeZoneId => ShopTimeZoneOptions.DefaultTimeZoneId;
        public DateTimeOffset ToShopLocal(DateTimeOffset utcInstant) => utcInstant;
        public DateOnly GetShopLocalDate(DateTimeOffset utcInstant) => DateOnly.FromDateTime(utcInstant.Date);
        public DateOnly Today() => new(2026, 9, 22);
        public DateTime ToDateTime(DateOnly shopLocalDate) => shopLocalDate.ToDateTime(new TimeOnly(0, 0));
        public DateTime TodayMidnight() => ToDateTime(Today());
    }

    private sealed class CapturingService : IWeeklyPerformanceService
    {
        public WeeklyReportFilter? Captured { get; private set; }

        public Task<WeeklyReportData> BuildAsync(WeeklyReportFilter filter, CancellationToken cancellationToken = default)
        {
            Captured = filter;
            return Task.FromResult(new WeeklyReportData
            {
                CurrentStart = filter.StartDate,
                CurrentEnd = filter.EndDate,
                PreviousStart = new DateTime(2026, 8, 9),
                PreviousEnd = new DateTime(2026, 8, 22),
                CurrentDayCount = 14,
                PreviousDayCount = 14,
                CurrentBucketCount = 1,
                PreviousBucketCount = 1,
                HasAnyMetricData = false,
                Cards = new DailyReportMetric[0],
                ComparisonRows = new DailyReportMetric[0],
                Trend = new WeeklyReportRow[0],
                WeeklyRows = new WeeklyReportRow[0],
                InteractionComposition = new DailyReportComposition { IsAvailable = false },
                ViewerComposition = new DailyReportComposition { IsAvailable = false },
                Notices = new string[0],
                SourceNotes = new DailyReportSourceNote[0],
                TimeZoneId = ShopTimeZoneOptions.DefaultTimeZoneId,
                TimeZoneLabel = "GMT+7"
            });
        }
    }

    private static WeeklyPerformanceController CreateController(CapturingService service)
        => new(service, new FakeShopTimeZone());

    [Fact]
    public async Task Index_WithoutDates_DefaultsTo_Last14Days_EndingYesterday()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        var result = await controller.Index(null, null);

        Assert.IsType<ViewResult>(result);
        var filter = service.Captured;
        Assert.NotNull(filter);
        Assert.Equal(new DateTime(2026, 9, 8), filter!.StartDate);   // 2026-09-22 - 13 days
        Assert.Equal(new DateTime(2026, 9, 21), filter.EndDate);     // yesterday
    }

    [Fact]
    public async Task Index_WithExactDates_PreservesThem_NoSnapping()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        await controller.Index(new DateTime(2026, 8, 26), new DateTime(2026, 9, 2));

        var filter = service.Captured;
        Assert.NotNull(filter);
        Assert.Equal(new DateTime(2026, 8, 26), filter!.StartDate);
        Assert.Equal(new DateTime(2026, 9, 2), filter.EndDate);
    }

    [Fact]
    public async Task Index_Strips_TimeComponent()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        await controller.Index(new DateTime(2026, 8, 23, 15, 42, 10), new DateTime(2026, 9, 5, 8, 1, 3));

        var filter = service.Captured;
        Assert.NotNull(filter);
        Assert.Equal(new DateTime(2026, 8, 23), filter!.StartDate);
        Assert.Equal(new DateTime(2026, 9, 5), filter.EndDate);
    }

    [Fact]
    public async Task Index_EndBeforeStart_ReturnsBadRequest_NeverSwaps()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        var result = await controller.Index(new DateTime(2026, 9, 5), new DateTime(2026, 8, 23));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(service.Captured); // service must not be called with an invalid range
    }

    [Fact]
    public async Task Index_EchoesEffectiveRange_ForTheDatePicker()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        await controller.Index(new DateTime(2026, 8, 23), new DateTime(2026, 9, 5));

        Assert.Equal("2026-08-23", controller.ViewData["StartDate"]);
        Assert.Equal("2026-09-05", controller.ViewData["EndDate"]);
    }

    [Fact]
    public async Task Report_ReturnsJson_OfTheSameServiceCall()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        var result = await controller.Report(new DateTime(2026, 8, 23), new DateTime(2026, 9, 5));

        var json = Assert.IsType<JsonResult>(result);
        var data = Assert.IsType<WeeklyReportData>(json.Value);
        Assert.Equal(new DateTime(2026, 8, 23), data.CurrentStart);
        Assert.Equal(new DateTime(2026, 9, 5), data.CurrentEnd);
    }

    [Fact]
    public async Task Report_InvalidRange_ReturnsBadRequest()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        var result = await controller.Report(new DateTime(2026, 9, 5), new DateTime(2026, 8, 23));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Index_DefaultRange_IsExactly14CalendarDays()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        await controller.Index(null, null);

        var filter = service.Captured;
        Assert.NotNull(filter);
        Assert.Equal(14, (filter!.EndDate - filter.StartDate).Days + 1);
    }
}