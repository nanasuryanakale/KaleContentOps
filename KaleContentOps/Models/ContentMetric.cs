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

    // Commerce/attribute metrics verified from actual shop video performance response
    // (App_Data/tiktok-api-sample.json -> data.videos[])
    public decimal? GmvAmount { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(10)]
    public string? GmvCurrency { get; set; }

    public long? ItemsSold { get; set; }

    public long? SkuOrders { get; set; }

    public decimal? AvgCustomers { get; set; }

    // Stored as a 0..1 rate, same convention as FullWatchRate (source "0.0533" = 5.33%)
    public decimal? ClickThroughRate { get; set; }

    // JSON array of strings, e.g. ["kaos","kaospria"]
    public string? HashtagsJson { get; set; }

    // JSON array of { id, name } objects
    public string? ProductsJson { get; set; }

    public DateTime? MetricStartDate { get; set; }

    public DateTime? MetricEndDate { get; set; }

    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    public ContentLog? ContentLog { get; set; }
}