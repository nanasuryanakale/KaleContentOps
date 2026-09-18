using KaleContentOps.Services.DailySummary;
using Microsoft.AspNetCore.Mvc;

namespace KaleContentOps.Controllers;

public class DailySummaryController : Controller
{
    private readonly IDailySummaryService _dailySummaryService;

    public DailySummaryController(IDailySummaryService dailySummaryService)
    {
        _dailySummaryService = dailySummaryService;
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
        var end = (endDate ?? DateTime.UtcNow.Date).Date;
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