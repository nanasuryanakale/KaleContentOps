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

    // Metric placeholders for metrics not yet stored in DB - keep nullable so UI shows "—"
    public long? Saves { get; set; }
    public long? NonFollowers { get; set; }
    public long? Male { get; set; }
    public long? Female { get; set; }
    public long? NonGender { get; set; }
    public long? Age18_24 { get; set; }
    public long? Age25_34 { get; set; }
    public long? Age35_44 { get; set; }
    public long? Age45_54 { get; set; }
    public long? Age55Plus { get; set; }
}
