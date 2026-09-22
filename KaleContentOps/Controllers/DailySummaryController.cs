using KaleContentOps.Services;
using KaleContentOps.Services.DailySummary;
using Microsoft.AspNetCore.Mvc;

namespace KaleContentOps.Controllers;

public class DailySummaryController : Controller
{
    private readonly IDailySummaryService _dailySummaryService;
    private readonly IShopTimeZone _shopTimeZone;

    public DailySummaryController(IDailySummaryService dailySummaryService, IShopTimeZone shopTimeZone)
    {
        _dailySummaryService = dailySummaryService;
        _shopTimeZone = shopTimeZone;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        DateTime? startDate,
        DateTime? endDate,
        bool includeArchived = false,
        bool includeAutoGmvInTotal = true,
        bool showNonKkColumns = true,
        bool showKkColumns = true,
        bool showAutoGmvColumns = true,
        CancellationToken cancellationToken = default)
    {
        // Issue A: default "today" must follow the shop reporting timezone (Asia/Jakarta),
        // not UTC and not the server machine timezone.
        var end = (endDate ?? _shopTimeZone.TodayMidnight()).Date;
        var start = (startDate ?? end.AddDays(-6)).Date;

        var model = await _dailySummaryService.BuildAsync(new DailySummaryFilter
        {
            StartDate = start,
            EndDate = end,
            IncludeArchived = includeArchived,
            IncludeAutoGmvInTotal = includeAutoGmvInTotal,
            ShowNonKkColumns = showNonKkColumns,
            ShowKkColumns = showKkColumns,
            ShowAutoGmvColumns = showAutoGmvColumns
        }, cancellationToken);

        return View(model);
    }
}