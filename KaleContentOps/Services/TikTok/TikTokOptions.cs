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
    // Details endpoint configuration (optional). If set and EnableDetails is true,
    // the service will call this endpoint to retrieve per-video engagement metrics.
    // Do NOT set in source control; provide via user secrets or environment.
    public string? DetailsPath { get; set; }

    // Enable calling Details API during sync. Default false to avoid accidental API calls.
    public bool EnableDetails { get; set; } = false;

    // Concurrency limit for Details calls (bounded). Default 5.
    // Concurrency limit for Details calls (bounded). Default 1 to be conservative.
    public int DetailsConcurrency { get; set; } = 1;

    // Max ContentLogs enriched per Details Sync run (per shop). Keeps routine runs bounded
    // and rate-limit safe: 0 or negative disables details sync entirely.
    public int DetailsSyncBatchSize { get; set; } = 20;

    // Request signing salt or other flags can be added later
    // OAuth configuration
    public string? ServiceId { get; set; }
    public string? RedirectUrl { get; set; }
}
