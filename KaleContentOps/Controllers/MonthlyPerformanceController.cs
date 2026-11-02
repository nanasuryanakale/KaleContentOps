using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Services;
using KaleContentOps.Services.CreativePerformance;
using Microsoft.AspNetCore.Mvc;

namespace KaleContentOps.Controllers;

/// <summary>
/// Creative Performance - Monthly Report (account-level analytics).
/// Controller responsibility ONLY: resolve the requested date range (with safe
/// defaults), reject invalid ranges, call <see cref="IMonthlyPerformanceService"/>
/// and hand the result to the View (or serialize it for the JSON endpoint).
/// No business calculation lives here - the service owns formulas, the View owns formatting.
/// </summary>
public class MonthlyPerformanceController : Controller
{
    private readonly IMonthlyPerformanceService _monthlyPerformanceService;
    private readonly IShopTimeZone _shopTimeZone;

    public MonthlyPerformanceController(IMonthlyPerformanceService monthlyPerformanceService, IShopTimeZone shopTimeZone)
    {
        _monthlyPerformanceService = monthlyPerformanceService;
        _shopTimeZone = shopTimeZone;
    }

    [HttpGet]
    public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate, CancellationToken cancellationToken = default)
    {
        var error = ResolveRange(startDate, endDate, out var start, out var end);
        if (error is not null)
        {
            return BadRequest(error);
        }

        var model = await BuildAsync(start, end, cancellationToken);

        // Echo the effective range so the picker binds to what was actually used.
        ViewData["StartDate"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ViewData["EndDate"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return View(model);
    }

    /// <summary>
    /// JSON variant of the exact same payload (same service call, same ranges) for
    /// AJAX consumers and integration tests. No separate business path exists.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Report(DateTime? startDate, DateTime? endDate, CancellationToken cancellationToken = default)
    {
        var error = ResolveRange(startDate, endDate, out var start, out var end);
        if (error is not null)
        {
            return BadRequest(error);
        }

        var model = await BuildAsync(start, end, cancellationToken);
        return Json(model);
    }

    private Task<MonthlyReportData> BuildAsync(DateTime start, DateTime end, CancellationToken cancellationToken)
        => _monthlyPerformanceService.BuildAsync(
            new MonthlyReportFilter { StartDate = start, EndDate = end },
            cancellationToken);

    /// <summary>
    /// Resolves the effective inclusive range. Defaults follow the Creative
    /// Performance convention fallback documented for this module: the last 14
    /// calendar days ending YESTERDAY in the shop timezone (Asia/Jakarta).
    /// Returns a validation error message, or null when the range is usable.
    /// </summary>
    private string? ResolveRange(DateTime? startDate, DateTime? endDate, out DateTime start, out DateTime end)
    {
        end = (endDate ?? _shopTimeZone.TodayMidnight().AddDays(-1)).Date;
        start = (startDate ?? end.AddDays(-13)).Date;

        return MonthlyReportDateRange.Validate(start, end);
    }
}