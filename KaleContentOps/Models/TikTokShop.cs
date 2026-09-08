using System.ComponentModel.DataAnnotations;

namespace KaleContentOps.Models;

public class TikTokShop
{
    public long Id { get; set; }

    [MaxLength(200)]
    public string? TikTokAccountId { get; set; }

    [MaxLength(500)]
    public string? ShopCipher { get; set; }

    [MaxLength(200)]
    public string? ShopId { get; set; }

    [MaxLength(200)]
    public string? ShopCode { get; set; }

    [MaxLength(500)]
    public string? ShopName { get; set; }

    [MaxLength(50)]
    public string? Region { get; set; }

    [MaxLength(100)]
    public string? SellerType { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Additional metadata can be added later
}
