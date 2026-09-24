using KaleContentOps.Models;
using System.Collections.Generic;

namespace KaleContentOps.ViewModels;

public class DailySummaryViewModel
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public List<DailySummaryRowViewModel> Rows { get; set; } = new List<DailySummaryRowViewModel>();

    // available content types for filter UI
    public List<ContentType> ContentTypes { get; set; } = new List<ContentType>();

    public bool IncludeArchived { get; set; }
    public bool IncludeAutoGmvInTotal { get; set; } = true;
    public bool ShowNonKkColumns { get; set; } = true;
    public bool ShowKkColumns { get; set; } = true;
    public bool ShowAutoGmvColumns { get; set; } = true;

    public int CalendarDays => (EndDate.Date - StartDate.Date).Days + 1;

    // Phase 6: targets come from Menu Targets (TargetService) per date. Page-level config
    // values (TotalDaily*Target) are gone; see per-row TotalContentTarget/TotalViewsTarget.
}

public class DailySummaryRowViewModel
{
    public DateTime Date { get; set; }

    public int TotalCount { get; set; }
    public long TotalViews { get; set; }

    public string TotalContentStatus { get; set; } = string.Empty;
    public string TotalViewsStatus { get; set; } = string.Empty;

    // Phase 6: combined daily target for THIS date, resolved by TargetService versioning
    // (weekly / 7). Used by client-side to re-evaluate the TOTAL status on the Include
    // Auto GMV toggle without a reload - the same targets the server used.
    public decimal TotalContentTarget { get; set; }
    public decimal TotalViewsTarget { get; set; }

    public int NonKkCount { get; set; }
    public long NonKkViews { get; set; }

    public int KkCount { get; set; }
    public long KkViews { get; set; }

    public int AutoGmvCount { get; set; }
    public long AutoGmvViews { get; set; }

    public string NonKkContentStatus { get; set; } = string.Empty;
    public string NonKkViewsStatus { get; set; } = string.Empty;
    public string KkContentStatus { get; set; } = string.Empty;
    public string KkViewsStatus { get; set; } = string.Empty;
}

public class DailySummaryContentTypeSummary
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public long TotalViews { get; set; }

    // computed
    public decimal AvgContentPerDay { get; set; }
    public decimal AvgViewsPerDay { get; set; }
}

public class DailySummaryComposition
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Percentage { get; set; }
}

public class DailySummaryTargetAchievement
{
    public string Code { get; set; } = string.Empty;
    public string ContentTypeName { get; set; } = string.Empty;
    public string MetricName { get; set; } = string.Empty;
    public decimal TargetPeriodValue { get; set; }
    public decimal ActualValue { get; set; }
    public decimal VarianceValue { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class DailySummaryPageModel
{
    public DailySummaryViewModel Data { get; set; } = new DailySummaryViewModel();
    public List<DailySummaryContentTypeSummary> TypeSummaries { get; set; } = new List<DailySummaryContentTypeSummary>();
    public List<DailySummaryComposition> Composition { get; set; } = new List<DailySummaryComposition>();
    public List<DailySummaryTargetAchievement> TargetAchievements { get; set; } = new List<DailySummaryTargetAchievement>();

    public bool ShowNonKk { get; set; } = true;
    public bool ShowKk { get; set; } = true;
    public bool ShowAutoGmv { get; set; } = true;
}
