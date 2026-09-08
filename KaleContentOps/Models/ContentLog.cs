using System.ComponentModel.DataAnnotations;

namespace KaleContentOps.Models;

public class ContentLog
{
    public long Id { get; set; }

    // TikTok identity
    [Required]
    [MaxLength(100)]
    public string VideoId { get; set; } = string.Empty;

    // TikTok shop context - nullable to support existing single-shop data
    public long? TikTokShopId { get; set; }

    public DateTime? VideoPostTime { get; set; }

    [MaxLength(500)]
    public string? Title { get; set; }

    [MaxLength(200)]
    public string? Username { get; set; }

    public int? Duration { get; set; }

    [MaxLength(1000)]
    public string? VideoUrl { get; set; }

    // Creator metadata
    [MaxLength(200)]
    public string? CreatorOpenId { get; set; }

    [MaxLength(200)]
    public string? CreatorUsername { get; set; }

    [MaxLength(200)]
    public string? CreatorNickname { get; set; }

    [MaxLength(50)]
    public string? AuthorType { get; set; }

    // Internal classifications
    public int? ContentTypeId { get; set; }
    public int? ProductionMethodId { get; set; }
    public int? PicId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ContentType? ContentType { get; set; }

    public ProductionMethod? ProductionMethod { get; set; }

    public MasterPic? Pic { get; set; }

    public TikTokShop? TikTokShop { get; set; }

    public ICollection<ContentMetric> Metrics { get; set; }
        = new List<ContentMetric>();
}