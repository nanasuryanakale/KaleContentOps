using System.Globalization;
using KaleContentOps.Services;
using KaleContentOps.Services.TeamPerformance;
using Microsoft.AspNetCore.Mvc;

namespace KaleContentOps.Controllers;

/// <summary>
/// Team Performance page (Phase 3 UI foundation).
/// Controller responsibility ONLY: authorization/access control (inherited from the
/// application's global fallback policy - see note below), safe period defaults, then
/// call <see cref="ITeamPerformanceService.BuildAsync"/> and hand the result to the View.
/// No totals, ranking, eligibility, aggregation or date maths live here.
///
/// ACCESS CONTROL ASSUMPTION: Team Performance ships with the SAME access model as the
/// sibling reporting menus (DailySummary, WinningContent) - authenticated users via the
/// Program.cs FallbackPolicy. No dedicated permission constant is invented here because
/// the exact Team Performance permission requirement is not defined by the locked rules.
/// </summary>
public class TeamPerformanceController : Controller
{
    private readonly ITeamPerformanceService _teamPerformanceService;
    private readonly IShopTimeZone _shopTimeZone;

    public TeamPerformanceController(ITeamPerformanceService teamPerformanceService, IShopTimeZone shopTimeZone)
    {
        _teamPerformanceService = teamPerformanceService;
        _shopTimeZone = shopTimeZone;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        DateTime? startDate,
        DateTime? endDate,
        CancellationToken cancellationToken = default)
    {
        // Same date convention as DailySummary / WinningContent: "today" follows the shop
        // reporting timezone (Asia/Jakarta) and the default window is the last 7 days
        // ending today. The selected period is inclusive [start, end].
        var end = (endDate ?? _shopTimeZone.TodayMidnight()).Date;
        var start = (startDate ?? end.AddDays(-6)).Date;

        var model = await _teamPerformanceService.BuildAsync(new TeamPerformanceFilter
        {
            StartDate = start,
            EndDate = end
        }, cancellationToken);

        // Echo the effective range so the View binds its picker to what was actually used.
        ViewData["StartDate"] = model.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ViewData["EndDate"] = model.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return View(model);
    }
}
