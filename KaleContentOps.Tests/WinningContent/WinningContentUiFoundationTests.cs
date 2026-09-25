using System;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Controllers;
using KaleContentOps.Services;
using KaleContentOps.Services.WinningContent;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace KaleContentOps.Tests.WinningContent;

/// <summary>
/// Phase 3A controller unit tests: date-range binding only (fake service).
/// The controller must apply the DailySummary-convention defaults (shop-local today,
/// last 7 days), pass the exact selected range to WinningContentFilter, and hand the
/// service result to the View without any business calculation of its own.
/// </summary>
public class WinningContentControllerTests
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

    private sealed class CapturingService : IWinningContentService
    {
        public WinningContentFilter? Captured { get; private set; }
        public WinningContentData Next { get; set; } = new();

        public Task<WinningContentData> BuildAsync(WinningContentFilter filter, CancellationToken cancellationToken = default)
        {
            Captured = filter;
            return Task.FromResult(Next);
        }
    }

    private static WinningContentController CreateController(CapturingService service) =>
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
    public async Task Index_WithDates_Passes_Exact_Range_To_Filter()
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
        var service = new CapturingService { Next = new WinningContentData() };
        var controller = CreateController(service);

        var result = Assert.IsType<ViewResult>(await controller.Index(null, null));

        Assert.Same(service.Next, result.Model);
    }
}
