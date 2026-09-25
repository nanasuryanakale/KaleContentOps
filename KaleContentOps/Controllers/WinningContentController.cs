using System.Globalization;
using KaleContentOps.Services;
using KaleContentOps.Services.WinningContent;
using Microsoft.AspNetCore.Mvc;

namespace KaleContentOps.Controllers;

/// <summary>
/// Winning Content page (Phase 3A UI foundation).
/// Controller responsibility ONLY: receive the selected date range, apply safe
/// defaults, call IWinningContentService.BuildAsync and hand the result to the View.
/// No business calculation (ER, baseline, multiplier, median, below-median,
/// composition) lives here - all analytics come from the service result.
/// </summary>
public class WinningContentController : Controller
{
    private readonly IWinningContentService _winningContentService;
    private readonly IShopTimeZone _shopTimeZone;

    public WinningContentController(IWinningContentService winningContentService, IShopTimeZone shopTimeZone)
    {
        _winningContentService = winningContentService;
        _shopTimeZone = shopTimeZone;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        DateTime? startDate,
        DateTime? endDate,
        CancellationToken cancellationToken = default)
    {
        // Same date convention as DailySummary: "today" follows the shop reporting
        // timezone (Asia/Jakarta) and the default window is the last 7 days ending today.
        // Selected-period semantics (inclusive [start, end]) stay exactly the Phase 2A ones.
        var end = (endDate ?? _shopTimeZone.TodayMidnight()).Date;
        var start = (startDate ?? end.AddDays(-6)).Date;

        var model = await _winningContentService.BuildAsync(new WinningContentFilter
        {
            StartDate = start,
            EndDate = end
        }, cancellationToken);

        // Echo the effective range so the View binds inputs to what was actually used.
        ViewData["StartDate"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ViewData["EndDate"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return View(model);
    }
}
