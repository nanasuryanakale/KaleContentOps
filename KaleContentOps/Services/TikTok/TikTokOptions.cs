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

    // Historical P1 scheduler configuration
    // Default disabled to avoid accidental API flood on deploy
    public bool EnableHistoricalDetailsP1Scheduler { get; set; } = false;
    public int HistoricalDetailsP1BatchSize { get; set; } = 10;
    public int HistoricalDetailsP1IntervalMinutes { get; set; } = 15;

    // ---- Persistent batch sync (Admin TikTok: Sync Video over a date range) ----
    // Worker poll interval when no runnable job exists.
    public int BatchSyncPollSeconds { get; set; } = 3;
    // Attempts per collection page before the job is marked Failed (bounded retries).
    public int BatchSyncMaxAttempts { get; set; } = 5;
    // Attempts per video item before the item is marked Failed.
    public int BatchSyncMaxItemAttempts { get; set; } = 3;
    // Consecutive rate-limit pauses (no progress in between) after which the job is no longer
    // resumed indefinitely: once this budget is exhausted AND the max run time below is reached,
    // the still-untried items are recorded honestly as NotAttempted and the job goes terminal.
    public int BatchSyncMaxRateLimitPauses { get; set; } = 5;
    // Hard bound on how long one job may keep being parked/resumed for rate limiting, measured
    // from its first claim (StartedAt). Prevents an unbounded park/resume loop.
    public int BatchSyncMaxRunMinutes { get; set; } = 720;
    // Upper bound applied to a single rate-limit cooldown, including one derived from a valid
    // _Retry-After_ response header (that header is honored, never blindly trusted).
    public int BatchSyncMaxCooldownSeconds { get; set; } = 900;
    // Exponential backoff base in seconds: base * 2^(attempt-1), capped at 300s.
    public int BatchSyncRetryBaseSeconds { get; set; } = 5;
    // Minutes without a row touch after which an active job is considered abandoned.
    public int BatchSyncLeaseMinutes { get; set; } = 10;
    // Hard pagination guard per job.
    public int BatchSyncMaxPages { get; set; } = 500;
    // Maximum accepted date range (inclusive) for one job.
    public int BatchSyncMaxRangeDays { get; set; } = 366;

    // Request signing salt or other flags can be added later
    // OAuth configuration
    public string? ServiceId { get; set; }
    public string? RedirectUrl { get; set; }
}
