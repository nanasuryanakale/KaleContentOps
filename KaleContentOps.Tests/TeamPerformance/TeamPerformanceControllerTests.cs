using System;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Controllers;
using KaleContentOps.Services;
using KaleContentOps.Services.TeamPerformance;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace KaleContentOps.Tests.TeamPerformance;

/// <summary>
/// Controller unit tests: the controller only applies defaults, normalizes to calendar
/// dates and passes the exact range to the service - no business calculation. Mirrors the
/// WinningContentControllerTests / DailySummary conventions.
/// </summary>
public class TeamPerformanceControllerTests
{
    private sealed class FakeShopTimeZone : IShopTimeZone
    {
        public string TimeZoneId => "Asia/Jakarta";
        public DateTimeOffset ToShopLocal(DateTimeOffset utcInstant) => utcInstant;
        public DateOnly GetShopLocalDate(DateTimeOffset utcInstant) => DateOnly.FromDateTime(utcInstant.Date);
        public DateOnly Today() => new(2026, 9, 25);
        public DateTime ToDateTime(DateOnly shopLocalDate) => shopLocalDate.ToDateTime(new TimeOnly(0, 0));
        public DateTime TodayMidnight() => ToDateTime(Today());
    }

    private sealed class CapturingService : ITeamPerformanceService
    {
        public TeamPerformanceFilter? Captured { get; private set; }
        public TeamPerformanceViewModel Next { get; set; } = new();

        public Task<TeamPerformanceViewModel> BuildAsync(TeamPerformanceFilter filter, CancellationToken cancellationToken = default)
        {
            Captured = filter;
            return Task.FromResult(Next);
        }
    }

    private static TeamPerformanceController CreateController(CapturingService service) =>
        new(service, new FakeShopTimeZone());

    [Fact]
    public async Task Index_WithoutDates_Uses_Last7Days_ShopLocal_Defaults()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        var result = await controller.Index(null, null);

        Assert.IsType<ViewResult>(result);
        var filter = service.Captured;
        Assert.NotNull(filter);
        Assert.Equal(new DateTime(2026, 9, 19), filter!.StartDate);   // 2026-09-25 - 6 days
        Assert.Equal(new DateTime(2026, 9, 25), filter.EndDate);
    }

    [Fact]
    public async Task Index_WithDates_Strips_Time_And_Passes_Exact_Range()
    {
        var service = new CapturingService();
        var controller = CreateController(service);

        await controller.Index(new DateTime(2026, 9, 1, 15, 42, 10), new DateTime(2026, 9, 7, 8, 1, 3));

        var filter = service.Captured;
        Assert.NotNull(filter);
        Assert.Equal(new DateTime(2026, 9, 1), filter!.StartDate);
        Assert.Equal(new DateTime(2026, 9, 7), filter.EndDate);
    }

    [Fact]
    public async Task Index_Returns_Service_Model_To_View()
    {
        var service = new CapturingService { Next = new TeamPerformanceViewModel() };
        var controller = CreateController(service);

        var result = Assert.IsType<ViewResult>(await controller.Index(null, null));

        Assert.Same(service.Next, result.Model);
    }
}
