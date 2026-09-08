using System.ComponentModel.DataAnnotations;

namespace KaleContentOps.Models;

public class TikTokCredential
{
    public long Id { get; set; }

    [MaxLength(200)]
    public string? AppKey { get; set; }

    // Encrypted tokens - do NOT store raw secrets in source control.
    [MaxLength(2000)]
    public string? EncryptedAccessToken { get; set; }

    [MaxLength(2000)]
    public string? EncryptedRefreshToken { get; set; }

    public DateTime? ExpiresAt { get; set; }
    public DateTime? RefreshExpiresAt { get; set; }

    [MaxLength(200)]
    public string? OpenId { get; set; }

    [MaxLength(500)]
    public string? SellerName { get; set; }

    [MaxLength(50)]
    public string? Region { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
