using System;

namespace KaleContentOps.ViewModels;

public class ContentLogListItem
{
    public long Id { get; set; }

    public DateTime? VideoPostTime { get; set; }

    public int? ContentTypeId { get; set; }

    public string? ContentTypeCode { get; set; }

    public string? ContentTypeName { get; set; }

    public int? ProductionMethodId { get; set; }

    public string? ProductionMethodName { get; set; }

    public int? PicId { get; set; }

    public string? Title { get; set; }

    public string? VideoUrl { get; set; }

    // Latest metric values (nullable)
    public long? Views { get; set; }

    public DateTime? LatestMetricCapturedAt { get; set; }

    // other metric columns used by the index view (keep nullable)
    public long? Reach { get; set; }
    public decimal? AverageWatch { get; set; }
    public decimal? FullWatchRate { get; set; }
    public long? Likes { get; set; }
    public long? Comments { get; set; }
    public long? Shares { get; set; }
    public long? NewFollowers { get; set; }

    // Raw latest-metric demographics JSON (transport only: parsed into the demographic
    // properties below after materialization; not consumed directly by the view).
    // Values inside are viewer-share percentages in the 0..1 range, as returned by TikTok.
    public string? DemographicsJson { get; set; }

    // Raw latest-metric commerce JSON transports (parsed after materialization)
    public string? HashtagsJson { get; set; }
    public string? ProductsJson { get; set; }

    // Demographic viewer-share percentages (0..1 decimals), parsed from DemographicsJson.
    public decimal? Male { get; set; }
    public decimal? Female { get; set; }
    public decimal? NonGender { get; set; }
    public decimal? Age18_24 { get; set; }
    public decimal? Age25_34 { get; set; }
    public decimal? Age35_44 { get; set; }
    public decimal? Age45_54 { get; set; }
    public decimal? Age55Plus { get; set; }

    // Viewer country distribution parsed from DemographicsJson "countries" object.
    // Key = ISO country code (blank codes are never persisted), value = viewer share 0..1.
    // Sorted by share descending. Null when the persisted JSON has no countries data.
    public List<KeyValuePair<string, decimal>>? Countries { get; set; }

    // ---- NEW_FOLLOWER population (strictly separate from the VIEWERS fields above) ----
    // Raw NEW_FOLLOWER demographics JSON (transport only: parsed into the Nf* properties
    // below after materialization). Same serialized shape as DemographicsJson (keys:
    // male / female / no_gender / ages{18-24..55+} / countries{ISO: share}) but produced by
    // TikTokVideoService.MapDetailsMetricsToContentMetric from the viewer_profile entry of
    // type NEW_FOLLOWER - never from the VIEWERS entry.
    public string? NewFollowerDemographicsJson { get; set; }

    // NEW_FOLLOWER demographic shares (0..1 decimals), parsed from NewFollowerDemographicsJson.
    public decimal? NfMale { get; set; }
    public decimal? NfFemale { get; set; }
    public decimal? NfNonGender { get; set; }
    public decimal? NfAge18_24 { get; set; }
    public decimal? NfAge25_34 { get; set; }
    public decimal? NfAge35_44 { get; set; }
    public decimal? NfAge45_54 { get; set; }
    public decimal? NfAge55Plus { get; set; }

    // NEW_FOLLOWER country distribution (same shape/meaning as Countries, other population).
    public List<KeyValuePair<string, decimal>>? NfCountries { get; set; }

    // Metrics not present in the actual TikTok details response - stay null so UI shows "—".
    public long? Saves { get; set; }
    public long? NonFollowers { get; set; }

    // Commerce/attribute metrics from the shop video performance response
    // (verified in App_Data/tiktok-api-sample.json -> data.videos[])
    public decimal? GmvAmount { get; set; }
    public string? GmvCurrency { get; set; }
    public long? ItemsSold { get; set; }
    public long? SkuOrders { get; set; }
    public decimal? AvgCustomers { get; set; }
    // 0..1 rate, same convention as FullWatchRate; UI renders as percentage
    public decimal? ClickThroughRate { get; set; }

    // Parsed from HashtagsJson / ProductsJson after materialization (transport pattern
    // identical to DemographicsJson). Null when absent.
    public List<string>? Hashtags { get; set; }
    public List<ContentLogProductItem>? Products { get; set; }
}

public class ContentLogProductItem
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}
