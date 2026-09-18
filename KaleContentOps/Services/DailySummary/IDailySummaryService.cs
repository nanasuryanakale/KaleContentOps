using KaleContentOps.ViewModels;

namespace KaleContentOps.Services.DailySummary;

public interface IDailySummaryService
{
    Task<DailySummaryPageModel> BuildAsync(DailySummaryFilter filter, CancellationToken cancellationToken = default);
}

public sealed class DailySummaryFilter
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IncludeArchived { get; set; }
    public bool IncludeAutoGmvInTotal { get; set; } = true;
    public bool ShowNonKkColumns { get; set; } = true;
    public bool ShowKkColumns { get; set; } = true;
    public bool ShowAutoGmvColumns { get; set; } = true;
}
