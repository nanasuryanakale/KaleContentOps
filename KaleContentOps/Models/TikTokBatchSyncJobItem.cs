using System;

namespace KaleContentOps.Models;

/// <summary>
/// Individual item in a batch sync job, representing one video_id and its sync progress.
/// </summary>
public class TikTokBatchSyncJobItem
{
    public long Id { get; set; }

    /// <summary>
    /// Foreign key to TikTokBatchSyncJob.Id
    /// </summary>
    public long TikTokBatchSyncJobId { get; set; }
    public TikTokBatchSyncJob? TikTokBatchSyncJob { get; set; }

    /// <summary>
    /// TikTok video_id to sync.
    /// </summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>
    /// Current item status:
    /// - Pending: waiting for worker to process.
    /// - Processing: worker claimed the item, in-flight HTTP/database operation.
    /// - Succeeded: details fetched, metrics saved, content type reclassified (if applicable).
    /// - Failed: non-retriable error (4xx, invalid video, etc.) or exhausted retry limit.
    /// </summary>
    public TikTokBatchSyncJobItemStatus Status { get; set; } = TikTokBatchSyncJobItemStatus.Pending;

    /// <summary>
    /// Number of times this item has been processed (attempted).
    /// Incremented on each Processing→(Succeeded|Failed) transition.
    /// Used to enforce maximum retry limit (e.g., stop after 3 attempts).
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Last error message while processing this item.
    /// Truncated or sanitized; does not include credential/token details.
    /// Null if succeeded.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Timestamp when this item was last processed (transitioned to Succeeded or Failed).
    /// Null if never processed.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Item creation timestamp (UTC); typically same as job creation or collection phase.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Item status enumeration for TikTokBatchSyncJobItem.
/// </summary>
public enum TikTokBatchSyncJobItemStatus
{
    Pending = 0,
    Processing = 1,
    Succeeded = 2,
    Failed = 3
}
