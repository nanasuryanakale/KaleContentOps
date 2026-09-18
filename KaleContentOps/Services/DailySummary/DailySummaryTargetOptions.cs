namespace KaleContentOps.Services.DailySummary;

public sealed class DailySummaryTargetOptions
{
    public const string SectionName = "DailySummaryTargets";

    public DailySummaryCategoryTarget NonKk { get; set; } = new();
    public DailySummaryCategoryTarget KeranjangKuning { get; set; } = new();
}

public sealed class DailySummaryCategoryTarget
{
    public decimal DailyContentTarget { get; set; }
    public decimal ViewsPerContentTarget { get; set; }

    public decimal DailyViewsTarget => DailyContentTarget * ViewsPerContentTarget;
}
