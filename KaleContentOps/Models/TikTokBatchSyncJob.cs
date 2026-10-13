using System;
using System.Collections.Generic;

namespace KaleContentOps.Models;

/// <summary>
/// Persistent job for batch synchronization of TikTok video performance details
/// over a date range. Each job collects video_ids via list API, then processes
/// details for each video sequentially with retry and rate-limit handling.
/// </summary>
public class TikTokBatchSyncJob
{
    public long Id { get; set; }

    /// <summary>
    /// Foreign key to TikTokShop.Id
    /// </summary>
    public long TikTokShopId { get; set; }
    public TikTokShop? TikTokShop { get; set; }

    /// <summary>
    /// Start date (inclusive) of performance analytics period.
    /// Semantics: date range for API /analytics/shop_videos/performance query (analytics period, not video post time).
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// End date (exclusive in API call; inclusive in UI) of performance analytics period.
    /// Stored as the actual end date user selected; worker converts to end_date_lt (add 1 day) for API.
    /// </summary>
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Current job status:
    /// - Queued: newly created, waiting for worker to process.
    /// - CollectingIds: worker is fetching video_ids via list API pagination.
    /// - ProcessingDetails: list collection complete, worker processing details for each video_id.
    /// - Completed: all items succeeded; no errors.
    /// - CompletedWithErrors: processing finished but some items failed.
    /// - Failed: critical error during list collection; job abandoned.
    /// </summary>
    public TikTokBatchSyncJobStatus Status { get; set; } = TikTokBatchSyncJobStatus.Queued;

    /// <summary>
    /// Job creation timestamp (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When worker started processing the job (transitioned from Queued). Null if not yet started.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When worker completed the job (transitioned to Completed/CompletedWithErrors/Failed). Null if not yet complete.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Number of pages fetched from the list API (i.e., pagination loop iterations).
    /// </summary>
    public int TotalPagesCollected { get; set; }

    /// <summary>
    /// Total unique video_ids found across all pages of the list API.
    /// </summary>
    public int TotalVideoIdsFound { get; set; }

    /// <summary>
    /// Number of items currently in Pending status (awaiting processing).
    /// </summary>
    public int PendingItemsCount { get; set; }

    /// <summary>
    /// Number of items currently in Processing status (claimed by worker, in-flight).
    /// </summary>
    public int ProcessingItemsCount { get; set; }

    /// <summary>
    /// Number of items successfully synced (status=Succeeded).
    /// </summary>
    public int SucceededItemsCount { get; set; }

    /// <summary>
    /// Number of items failed after exhausting retries (status=Failed).
    /// </summary>
    public int FailedItemsCount { get; set; }

    /// <summary>
    /// Summary of the last error encountered during list collection or overall job execution.
    /// Truncated or sanitized; does not include credential/token details.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Related job items (video_ids and their sync status).
    /// </summary>
    public ICollection<TikTokBatchSyncJobItem>? Items { get; set; } = new List<TikTokBatchSyncJobItem>();
}

/// <summary>
/// Job status enumeration for TikTokBatchSyncJob.
/// </summary>
public enum TikTokBatchSyncJobStatus
{
    Queued = 0,
    CollectingIds = 1,
    ProcessingDetails = 2,
    Completed = 3,
    CompletedWithErrors = 4,
    Failed = 5
}
