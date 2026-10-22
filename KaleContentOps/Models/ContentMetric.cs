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

    // NEW_FOLLOWER viewer-profile demographics (separate population from DemographicsJson).
    // Same JSON shape: { male, female, no_gender, ages:{"18-24".."55+"}, countries:{CC:share} }.
    // Null when the response contains no NEW_FOLLOWER profile with parseable values.
    public string? NewFollowerDemographicsJson { get; set; }

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

    // Per-video LIST endpoint (analytics/202605) quality/attribution fields, verified
    // field-by-field against the live response (2026-10-22, 300 videos across 3 windows):
    //  - video_finish_rate: fraction of viewers finishing the video (0..1, range observed 0..1)
    //  - v_to_l_clicks: video-to-live clicks (count)
    //  - v_to_l_rate: verified = v_to_l_clicks / views (matched on 92-99 of 100 items per window)
    // Windowed by the sync request's start/end dates - each row carries its captured window
    // (MetricStartDate/MetricEndDate), never an all-time figure. Rates use the same 0..1
    // convention as FullWatchRate/ClickThroughRate. Null = field absent (never 0).
    public decimal? VideoFinishRate { get; set; }

    public long? VToLClicks { get; set; }

    public decimal? VToLRate { get; set; }

    // JSON array of strings, e.g. ["kaos","kaospria"]
    public string? HashtagsJson { get; set; }

    // JSON array of { id, name } objects
    public string? ProductsJson { get; set; }

    public DateTime? MetricStartDate { get; set; }

    public DateTime? MetricEndDate { get; set; }

    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    public ContentLog? ContentLog { get; set; }
}