using System;
using System.Collections.Generic;

namespace KaleContentOps.Services.TikTok;

/// <summary>
/// Serialized status of a batch sync job for the Admin TikTok status endpoint.
/// All counts are read from SQL Server (authoritative), not from in-memory state.
/// </summary>
public sealed class BatchSyncJobStatusDto
{
    public long JobId { get; set; }
    public long ShopId { get; set; }
    public string? ShopName { get; set; }

    /// <summary>Range as requested by the user (INCLUSIVE end date).</summary>
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    /// <summary>Raw job status: Queued | CollectingIds | ProcessingDetails | Completed | CompletedWithErrors | Failed.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Human-readable stage for the UI: Queued | Collecting IDs | Processing Details | Completed | Completed with errors | Failed.</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>True while the worker still has work to do (or will resume this job).</summary>
    public bool IsActive { get; set; }

    /// <summary>True only after ALL list pages were fetched (details never start before this).</summary>
    public bool CollectionCompleted { get; set; }

    public int TotalPagesCollected { get; set; }

    /// <summary>Total video ids queued so far (= total job items).</summary>
    public int TotalVideoIdsFound { get; set; }

    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }

    /// <summary>
    /// Items the job stopped before ever attempting (bounded rate-limit budget exhausted).
    /// Kept apart from <see cref="Failed"/> so an untried video is never reported as a failure.
    /// </summary>
    public int NotAttempted { get; set; }

    /// <summary>
    /// Videos from this job whose ContentLog still has no ContentTypeId because no
    /// classification signal (IsLive / HasCommerce / ProductsJson) was ever available.
    /// Honest state - never fabricated.
    /// </summary>
    public int UnclassifiedVideos { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Earliest time the worker will (re)try this job; null = immediately.</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// <see cref="NextAttemptAt"/> rendered in the shop reporting timezone with an explicit
    /// label (e.g. "14 Okt 2026 15:30 GMT+7"), or null. Never a manual UTC-offset computation.
    /// </summary>
    public string? NextAttemptAtDisplay { get; set; }

    /// <summary><see cref="CreatedAt"/> rendered in the shop reporting timezone with an explicit label.</summary>
    public string? CreatedAtDisplay { get; set; }

    /// <summary>IANA id of the shop reporting timezone used for the display fields.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>Explicit display label for the timezone (app convention: "GMT+7").</summary>
    public string TimeZoneLabel { get; set; } = string.Empty;

    /// <summary>Retry/backoff counter for the current phase.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Sanitized last error / failure summary. Never contains credentials.</summary>
    public string? LastError { get; set; }

    /// <summary>Up to 20 failed items for the UI error summary.</summary>
    public List<BatchSyncItemDto> FailedItems { get; set; } = new();
}

public sealed class BatchSyncItemDto
{
    public string VideoId { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
