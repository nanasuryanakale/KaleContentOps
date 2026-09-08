namespace KaleContentOps.Services.TikTok;

public class TikTokOptions
{
    // App credentials - do NOT store secrets in source control. Use user secrets / env vars.
    public string? AppKey { get; set; }
    public string? AppSecret { get; set; }

    // Base URLs
    public string? BaseUrl { get; set; }
    public string? AuthBaseUrl { get; set; }

    // Optional: default shop_cipher if known
    public string? DefaultShopCipher { get; set; }

    // Request signing salt or other flags can be added later
}
