namespace KaleContentOps.Models;

public class ContentMetric
{
    public long Id { get; set; }

    public long ContentLogId { get; set; }

    public long? Views { get; set; }

    public long? Reach { get; set; }

    public long? Likes { get; set; }

    public long? Comments { get; set; }

    public long? Shares { get; set; }

    public long? NewFollowers { get; set; }

    public decimal? AverageWatch { get; set; }

    public decimal? FullWatchRate { get; set; }

    public string? DemographicsJson { get; set; }

    public DateTime? MetricStartDate { get; set; }

    public DateTime? MetricEndDate { get; set; }

    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    public ContentLog? ContentLog { get; set; }
}