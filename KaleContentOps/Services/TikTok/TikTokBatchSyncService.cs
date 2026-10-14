using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KaleContentOps.Services.TikTok;

/// <summary>
/// Persistent batch video sync orchestrator (Menu Admin TikTok - "Sync Video Details").
///
/// Flow (all state lives in SQL Server, never in memory):
///   1. <see cref="StartJobAsync"/> validates shop + date range and creates a Queued job.
///   2. <see cref="ProcessNextJobAsync"/> claims a job (optimistic CAS on Status), collects
///      video ids page-by-page through the existing list API client, checkpointing
///      page_token after every page (resume after crash, dedup per job).
///   3. Only after collection completed, details are processed ONE video at a time through
///      the existing <see cref="TikTokDetailsSyncService"/> pipeline (ContentMetric persistence,
///      demographic parsing, rate-limit/backoff handling, classification).
///   4. Item/job status + counters are persisted after every step; terminal states are
///      Completed / CompletedWithErrors / Failed, computed from actual item results.
///
/// Claiming: <c>Status</c> is an EF concurrency token on both tables, so every transition
/// (Queued -> CollectingIds, Pending -> Processing, ...) is a compare-and-swap. Two workers
/// can never process the same job or the same item. An active job whose row lease
/// (<see cref="TikTokBatchSyncJob.UpdatedAt"/>) expired (crashed worker) is taken over via a
/// CAS flip back to Queued first, so takeover is also single-winner.
/// </summary>
public class TikTokBatchSyncService
{
    private readonly AppDbContext _db;
    private readonly ITikTokVideoService _videoService;
    private readonly TikTokDetailsSyncService _detailsService;
    private readonly ILogger<TikTokBatchSyncService> _logger;
    private readonly TikTokOptions _options;
    private readonly IShopTimeZone _shopTimeZone;

    public TikTokBatchSyncService(
        AppDbContext db,
        ITikTokVideoService videoService,
        TikTokDetailsSyncService detailsService,
        ILogger<TikTokBatchSyncService>? logger = null,
        IOptions<TikTokOptions>? options = null,
        IShopTimeZone? shopTimeZone = null)
    {
        _db = db;
        _videoService = videoService;
        _detailsService = detailsService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TikTokBatchSyncService>.Instance;
        _options = options?.Value ?? new TikTokOptions();
        // Same shared single timezone source as Daily Summary / Daily Performance (Asia/Jakarta).
        _shopTimeZone = shopTimeZone ?? new ShopTimeZone(Microsoft.Extensions.Options.Options.Create(new ShopTimeZoneOptions()));
    }

    /// <summary>Explicit display label for the shop reporting timezone (app convention: GMT+7).</summary>
    private string TimeZoneLabel => _shopTimeZone.TimeZoneId == ShopTimeZoneOptions.DefaultTimeZoneId
        ? "GMT+7"
        : _shopTimeZone.TimeZoneId;

    /// <summary>
    /// Formats a stored UTC instant in the shop reporting timezone with an explicit label.
    /// Uses the shared <see cref="IShopTimeZone"/> helper, never a manual offset.
    /// </summary>
    private string? FormatShopLocal(DateTime? utc)
    {
        if (!utc.HasValue) return null;
        var utcInstant = new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc));
        var local = _shopTimeZone.ToShopLocal(utcInstant);
        return local.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " " + TimeZoneLabel;
    }

    // ------------------------------------------------------------------
    // Creation / read API (used by the controller)
    // ------------------------------------------------------------------

    /// <summary>
    /// Validates the request and queues a new batch sync job.
    /// Throws ArgumentException / InvalidOperationException with a user-safe message on invalid input.
    /// </summary>
    public async Task<long> StartJobAsync(long shopId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        var start = startDate.Date;
        var end = endDate.Date;

        if (end < start)
            throw new ArgumentException("End date must be on or after start date.");

        var maxDays = Math.Max(1, _options.BatchSyncMaxRangeDays);
        if ((end - start).Days + 1 > maxDays)
            throw new ArgumentException($"Date range must not exceed {maxDays} days.");

        var shop = await _db.TikTokShops.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == shopId, cancellationToken)
            ?? throw new InvalidOperationException("Shop not found.");

        if (string.IsNullOrWhiteSpace(shop.ShopCipher))
            throw new InvalidOperationException("Shop has no authorized TikTok cipher. Run 'Sync Authorized Shops' first.");

        var activeJobId = await _db.TikTokBatchSyncJobs.AsNoTracking()
            .Where(j => j.TikTokShopId == shopId
                && (j.Status == TikTokBatchSyncJobStatus.Queued
                    || j.Status == TikTokBatchSyncJobStatus.CollectingIds
                    || j.Status == TikTokBatchSyncJobStatus.ProcessingDetails))
            .Select(j => (long?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (activeJobId.HasValue)
            throw new InvalidOperationException($"Shop already has an active batch sync job (#{activeJobId.Value}). Wait for it to finish before starting another.");

        var job = new TikTokBatchSyncJob
        {
            TikTokShopId = shopId,
            StartDate = start,
            EndDate = end,
            Status = TikTokBatchSyncJobStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };
        _db.TikTokBatchSyncJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Batch sync job {JobId} queued for shop {ShopId}: {Start}..{End} (inclusive analytics period).",
            job.Id, shopId, start, end);

        return job.Id;
    }

    public async Task<BatchSyncJobStatusDto?> GetStatusAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var job = await _db.TikTokBatchSyncJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        return job == null ? null : await BuildStatusAsync(job, cancellationToken);
    }

    public async Task<IReadOnlyList<BatchSyncJobStatusDto>> GetHistoryAsync(int take = 10, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 50);
        var jobs = await _db.TikTokBatchSyncJobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        var result = new List<BatchSyncJobStatusDto>(jobs.Count);
        foreach (var job in jobs)
        {
            result.Add(await BuildStatusAsync(job, cancellationToken));
        }
        return result;
    }

    private async Task<BatchSyncJobStatusDto> BuildStatusAsync(TikTokBatchSyncJob job, CancellationToken ct)
    {
        var shopName = await _db.TikTokShops.AsNoTracking()
            .Where(s => s.Id == job.TikTokShopId)
            .Select(s => s.ShopName ?? s.ShopCode ?? s.ShopId)
            .FirstOrDefaultAsync(ct);

        var counts = await _db.TikTokBatchSyncJobItems.AsNoTracking()
            .Where(i => i.TikTokBatchSyncJobId == job.Id)
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int CountOf(TikTokBatchSyncJobItemStatus status) =>
            counts.Where(c => c.Status == status).Sum(c => c.Count);

        var pending = CountOf(TikTokBatchSyncJobItemStatus.Pending);
        var processing = CountOf(TikTokBatchSyncJobItemStatus.Processing);
        var succeeded = CountOf(TikTokBatchSyncJobItemStatus.Succeeded);
        var failed = CountOf(TikTokBatchSyncJobItemStatus.Failed);
        var notAttempted = CountOf(TikTokBatchSyncJobItemStatus.NotAttempted);
        var total = pending + processing + succeeded + failed + notAttempted;

        // Honest unclassified count: videos from THIS job whose ContentLog has no type
        // because no signal ever became available. Chunked to keep SQL parameter counts safe.
        var videoIds = await _db.TikTokBatchSyncJobItems.AsNoTracking()
            .Where(i => i.TikTokBatchSyncJobId == job.Id)
            .Select(i => i.VideoId)
            .ToListAsync(ct);

        var unclassified = 0;
        foreach (var chunk in videoIds.Chunk(500))
        {
            unclassified += await _db.ContentLogs.AsNoTracking()
                .Where(cl => cl.TikTokShopId == job.TikTokShopId
                    && chunk.Contains(cl.VideoId)
                    && cl.ContentTypeId == null)
                .CountAsync(ct);
        }

        var failedItems = await _db.TikTokBatchSyncJobItems.AsNoTracking()
            .Where(i => i.TikTokBatchSyncJobId == job.Id && i.Status == TikTokBatchSyncJobItemStatus.Failed)
            .OrderBy(i => i.Id)
            .Take(20)
            .Select(i => new BatchSyncItemDto
            {
                VideoId = i.VideoId,
                AttemptCount = i.AttemptCount,
                LastError = i.LastError,
                ProcessedAt = i.ProcessedAt
            })
            .ToListAsync(ct);

        return new BatchSyncJobStatusDto
        {
            JobId = job.Id,
            ShopId = job.TikTokShopId,
            ShopName = shopName,
            StartDate = job.StartDate,
            EndDate = job.EndDate,
            Status = job.Status.ToString(),
            Phase = PhaseLabel(job.Status),
            IsActive = job.Status == TikTokBatchSyncJobStatus.Queued
                || job.Status == TikTokBatchSyncJobStatus.CollectingIds
                || job.Status == TikTokBatchSyncJobStatus.ProcessingDetails,
            CollectionCompleted = job.CollectionCompleted,
            TotalPagesCollected = job.TotalPagesCollected,
            TotalVideoIdsFound = total,
            Pending = pending,
            Processing = processing,
            Succeeded = succeeded,
            Failed = failed,
            NotAttempted = notAttempted,
            UnclassifiedVideos = unclassified,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            NextAttemptAt = job.NextAttemptAt,
            NextAttemptAtDisplay = FormatShopLocal(job.NextAttemptAt),
            CreatedAtDisplay = FormatShopLocal(job.CreatedAt),
            AttemptCount = job.AttemptCount,
            LastError = job.LastError,
            TimeZoneId = _shopTimeZone.TimeZoneId,
            TimeZoneLabel = TimeZoneLabel,
            FailedItems = failedItems
        };
    }

    private static string PhaseLabel(TikTokBatchSyncJobStatus status) => status switch
    {
        TikTokBatchSyncJobStatus.Queued => "Queued",
        TikTokBatchSyncJobStatus.CollectingIds => "Collecting IDs",
        TikTokBatchSyncJobStatus.ProcessingDetails => "Processing Details",
        TikTokBatchSyncJobStatus.Completed => "Completed",
        TikTokBatchSyncJobStatus.CompletedWithErrors => "Completed with errors",
        TikTokBatchSyncJobStatus.Failed => "Failed",
        _ => status.ToString()
    };

    // ------------------------------------------------------------------
    // Worker entry point
    // ------------------------------------------------------------------

    /// <summary>
    /// Claims and fully processes at most one runnable job. Returns the processed job id,
    /// or null when nothing is runnable right now (nothing queued, lease held by another
    /// worker, or job waiting for its NextAttemptAt backoff).
    /// </summary>
    public async Task<long?> ProcessNextJobAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var leaseCutoff = now - TimeSpan.FromMinutes(Math.Max(1, _options.BatchSyncLeaseMinutes));

        var candidates = await _db.TikTokBatchSyncJobs.AsNoTracking()
            .Where(j => j.NextAttemptAt == null || j.NextAttemptAt <= now)
            .Where(j => j.Status == TikTokBatchSyncJobStatus.Queued
                || ((j.Status == TikTokBatchSyncJobStatus.CollectingIds
                     || j.Status == TikTokBatchSyncJobStatus.ProcessingDetails)
                    // Active job is claimable when it was deliberately parked for a retry
                    // (NextAttemptAt set and now due - outer filter) OR when its worker lease
                    // expired (crashed worker). A fresh, unparked active job is not touched.
                    && (j.NextAttemptAt != null || j.UpdatedAt == null || j.UpdatedAt <= leaseCutoff)))
            .OrderBy(j => j.CreatedAt)
            .ThenBy(j => j.Id)
            .Select(j => j.Id)
            .Take(5)
            .ToListAsync(cancellationToken);

        foreach (var jobId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await TryClaimJobAsync(jobId, cancellationToken))
                continue;

            var job = await _db.TikTokBatchSyncJobs
                .FirstAsync(j => j.Id == jobId, cancellationToken);

            try
            {
                if (!job.CollectionCompleted)
                    await CollectVideoIdsAsync(job, cancellationToken);

                if (job.CollectionCompleted && job.Status != TikTokBatchSyncJobStatus.Failed)
                    await ProcessDetailsAsync(job, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown/restart: leave the job in its current phase. The row lease lets a
                // worker claim it again after restart; stuck Processing items are released then.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch sync job {JobId} failed unexpectedly.", job.Id);
                await FailJobAsync(job, $"Unexpected error: {ex.Message}", cancellationToken);
            }

            return job.Id;
        }

        return null;
    }

    /// <summary>
    /// Optimistic claim (CAS on the Status concurrency token). Returns true only when THIS
    /// caller won the job. Public for tests that verify single-winner semantics.
    /// </summary>
    public async Task<bool> TryClaimJobAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var lease = TimeSpan.FromMinutes(Math.Max(1, _options.BatchSyncLeaseMinutes));

        for (var round = 0; round < 3; round++)
        {
            var job = await _db.TikTokBatchSyncJobs
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            if (job == null) return false;

            // Backoff gate: not allowed to run yet.
            if (job.NextAttemptAt.HasValue && job.NextAttemptAt.Value > now)
                return false;

            var isActive = job.Status == TikTokBatchSyncJobStatus.CollectingIds
                || job.Status == TikTokBatchSyncJobStatus.ProcessingDetails;
            // Lease expired (crashed worker) OR the job was deliberately parked for a bounded
            // retry and its NextAttemptAt is now due (the gate above already guaranteed due).
            var stale = isActive && (
                (job.UpdatedAt == null || job.UpdatedAt.Value + lease < now)
                || job.NextAttemptAt.HasValue);

            if (job.Status == TikTokBatchSyncJobStatus.Queued)
            {
                job.Status = job.CollectionCompleted
                    ? TikTokBatchSyncJobStatus.ProcessingDetails
                    : TikTokBatchSyncJobStatus.CollectingIds;
                job.StartedAt ??= now;
                job.UpdatedAt = now;
                return await TrySaveAsync(cancellationToken);
            }

            if (stale)
            {
                // Crash takeover: CAS flip active -> Queued first so exactly ONE worker can
                // proceed to claim it in the next round (two workers can't both win the flip).
                job.Status = TikTokBatchSyncJobStatus.Queued;
                job.UpdatedAt = now;
                if (!await TrySaveAsync(cancellationToken)) return false;
                continue;
            }

            return false;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Phase 1: video-id collection (pagination with checkpoint + dedup)
    // ------------------------------------------------------------------

    private async Task CollectVideoIdsAsync(TikTokBatchSyncJob job, CancellationToken ct)
    {
        var shop = await _db.TikTokShops.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == job.TikTokShopId, ct);
        if (shop == null || string.IsNullOrWhiteSpace(shop.ShopCipher))
        {
            await FailJobAsync(job, "Shop is not an authorized TikTok shop (missing ShopCipher). Run 'Sync Authorized Shops' first.", ct);
            return;
        }

        var cipher = shop.ShopCipher;
        var startIso = job.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // UI End Date is INCLUSIVE; the API contract is end_date_lt (EXCLUSIVE) -> +1 day.
        var endExclusiveIso = job.EndDate.Date.AddDays(1)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (job.TotalPagesCollected >= Math.Max(1, _options.BatchSyncMaxPages))
            {
                await FailJobAsync(job, $"Pagination guard: reached the maximum of {_options.BatchSyncMaxPages} pages without completing collection.", ct);
                return;
            }

            var previousToken = job.CurrentPageToken;

            TikTokVideoListPage page;
            try
            {
                page = await _videoService.FetchVideoListPageAsync(cipher, startIso, endExclusiveIso, previousToken, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!await HandleCollectionFailureAsync(job, ex, ct)) return;
                await DelayUntilNextAttemptAsync(job, ct);
                continue; // checkpoint unchanged -> same page is retried
            }

            // Pagination must advance; a repeating token would loop forever.
            if (!string.IsNullOrWhiteSpace(page.NextPageToken) && page.NextPageToken == previousToken)
            {
                await FailJobAsync(job, "Pagination token did not advance; aborting to avoid an infinite loop.", ct);
                return;
            }

            // Persist the page through the SAME upsert pipeline as the classic list sync:
            // ContentLog insert/update keyed by (shop, video_id) - historical patching at
            // list level - plus list-phase ContentMetric and signal parsing/classification.
            try
            {
                await _videoService.ApplyVideoListPageAsync(cipher, page, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Do NOT advance the checkpoint: the page is re-fetched and re-applied
                // (Apply is idempotent), so no video can be skipped after a failure.
                if (!await HandleCollectionFailureAsync(job, ex, ct)) return;
                await DelayUntilNextAttemptAsync(job, ct);
                continue;
            }

            // Queue video ids persistently with dedup (unique index on (JobId, VideoId)
            // enforces this at database level as well).
            var existingIds = new HashSet<string>(
                await _db.TikTokBatchSyncJobItems
                    .Where(i => i.TikTokBatchSyncJobId == job.Id)
                    .Select(i => i.VideoId)
                    .ToListAsync(ct),
                StringComparer.Ordinal);

            var now = DateTime.UtcNow;
            foreach (var videoId in page.VideoIds)
            {
                if (existingIds.Add(videoId))
                {
                    _db.TikTokBatchSyncJobItems.Add(new TikTokBatchSyncJobItem
                    {
                        TikTokBatchSyncJobId = job.Id,
                        VideoId = videoId,
                        Status = TikTokBatchSyncJobItemStatus.Pending,
                        CreatedAt = now
                    });
                }
            }

            // Checkpoint AFTER the page content is fully persisted.
            job.TotalPagesCollected++;
            job.CurrentPageToken = page.NextPageToken;
            job.TotalVideoIdsFound = existingIds.Count;
            job.AttemptCount = 0;
            job.NextAttemptAt = null;
            job.UpdatedAt = now;

            if (string.IsNullOrWhiteSpace(page.NextPageToken))
            {
                job.CollectionCompleted = true;
                job.CurrentPageToken = null;
                // Phase transition in the SAME atomic save: the UI/status endpoint must show
                // "Processing Details" (not "Collecting IDs") once details work may begin.
                job.Status = TikTokBatchSyncJobStatus.ProcessingDetails;
            }

            await _db.SaveChangesAsync(ct);

            if (job.CollectionCompleted)
            {
                _logger.LogInformation(
                    "Batch sync job {JobId}: id collection completed. {Pages} page(s), {Count} video id(s).",
                    job.Id, job.TotalPagesCollected, job.TotalVideoIdsFound);
                return;
            }
        }
    }

    private async Task<bool> HandleCollectionFailureAsync(TikTokBatchSyncJob job, Exception ex, CancellationToken ct)
    {
        var kind = ClassifyError(ex);
        var maxAttempts = Math.Max(1, _options.BatchSyncMaxAttempts);
        job.AttemptCount++;
        job.UpdatedAt = DateTime.UtcNow;
        job.LastError = Sanitize(GetDetailedErrorMessage(ex));

        // Bounded retries: exhausting the budget (or a permanent error) fails the job. A failed
        // request is NEVER treated as an empty page / completed collection / success.
        if (kind == ErrorKind.Permanent || job.AttemptCount >= maxAttempts)
        {
            await FailJobAsync(job,
                $"Video ID collection failed after {job.AttemptCount} attempt(s): {Sanitize(GetDetailedErrorMessage(ex))}", ct);
            return false;
        }

        if (kind == ErrorKind.RateLimit || kind == ErrorKind.Network)
        {
            // Park the job: record when the worker may try again, then release it. A DNS/network
            // outage or a rate limit must not burn the whole retry budget in a tight in-process
            // loop; NextAttemptAt becomes the real recovery gate for the background worker.
            job.NextAttemptAt = DateTime.UtcNow + ComputeBackoff(job.AttemptCount);
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(
                "Batch sync job {JobId}: collection attempt {Attempt}/{Max} failed ({Kind}); parked until {Next:u}. {Message}",
                job.Id, job.AttemptCount, maxAttempts, kind, job.NextAttemptAt, Sanitize(GetDetailedErrorMessage(ex)));
            return false; // caller returns -> worker resumes from NextAttemptAt
        }

        // Transient HTTP error (server responded, e.g. 5xx): retry in-process; no NextAttemptAt.
        job.NextAttemptAt = null;
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning(
            "Batch sync job {JobId}: collection attempt {Attempt}/{Max} failed ({Kind}); retrying. {Message}",
            job.Id, job.AttemptCount, maxAttempts, kind, Sanitize(GetDetailedErrorMessage(ex)));
        return true;
    }

    /// <summary>
    /// Returns a detailed error message that distinguishes DNS/network failures from HTTP errors.
    /// DNS/connection failures before HTTP response get a specific prefix.
    /// </summary>
    private string GetDetailedErrorMessage(Exception ex)
    {
        if (ex == null) return "Unknown error";

        if (IsNetworkFailure(ex))
        {
            return "DNS or network connection failure: " + (ex.Message ?? "unable to resolve hostname or connect");
        }

        return ex.Message ?? "Unknown error";
    }

    // ------------------------------------------------------------------
    // Phase 2: details processing (one video at a time, existing pipeline)
    // ------------------------------------------------------------------

    private async Task ProcessDetailsAsync(TikTokBatchSyncJob job, CancellationToken ct)
    {
        var shop = await _db.TikTokShops.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == job.TikTokShopId, ct);
        if (shop == null || string.IsNullOrWhiteSpace(shop.ShopCipher))
        {
            await FailJobAsync(job, "Shop is not an authorized TikTok shop (missing ShopCipher).", ct);
            return;
        }

        // Restart recovery: we hold the exclusive job claim, so any item still marked
        // Processing belongs to a crashed worker and can be safely released back to Pending.
        var stuck = await _db.TikTokBatchSyncJobItems
            .Where(i => i.TikTokBatchSyncJobId == job.Id && i.Status == TikTokBatchSyncJobItemStatus.Processing)
            .ToListAsync(ct);
        if (stuck.Count > 0)
        {
            foreach (var item in stuck)
            {
                item.Status = TikTokBatchSyncJobItemStatus.Pending;
                item.LastError = "Recovered after worker interruption.";
            }
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Batch sync job {JobId}: released {Count} stuck Processing item(s).", job.Id, stuck.Count);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var item = await _db.TikTokBatchSyncJobItems
                .Where(i => i.TikTokBatchSyncJobId == job.Id && i.Status == TikTokBatchSyncJobItemStatus.Pending)
                .OrderBy(i => i.Id)
                .FirstOrDefaultAsync(ct);
            if (item == null) break;

            // Claim the item (CAS on Status): a second worker can never take it.
            item.Status = TikTokBatchSyncJobItemStatus.Processing;
            job.UpdatedAt = DateTime.UtcNow;
            if (!await TrySaveAsync(ct)) continue;

            // Stable identifier per existing model: (TikTokShopId, VideoId).
            var log = await _db.ContentLogs
                .FirstOrDefaultAsync(cl => cl.TikTokShopId == shop.Id && cl.VideoId == item.VideoId, ct);
            if (log == null)
            {
                // Only reached if the list-phase upsert was skipped (e.g. recovery edge case):
                // create a minimal shell keyed by the stable identifier - never a duplicate.
                log = new ContentLog
                {
                    VideoId = item.VideoId,
                    TikTokShopId = shop.Id,
                    ProductionMethodId = 1, // seeded default "Self Produce", mirrors list sync
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.ContentLogs.Add(log);
                await _db.SaveChangesAsync(ct);
            }

            TikTokDetailsSyncService.SingleVideoSyncOutcome outcome;
            try
            {
                outcome = await _detailsService.SyncSingleContentLogDetailedAsync(log.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = new TikTokDetailsSyncService.SingleVideoSyncOutcome { ErrorMessage = ex.Message };
            }

            var now = DateTime.UtcNow;
            var transientRetry = false;

            if (outcome.Throttled)
            {
                // Rate limit (HTTP 429 after the client's own backoff, or business code 36009002):
                // release the item untouched and park the WHOLE job with a bounded cooldown.
                //
                // Items that were never tried keep status Pending. Previously every remaining
                // item was stamped Failed ("Rate limit retry budget exhausted"), which turned one
                // throttle into thousands of fake item failures.
                item.Status = TikTokBatchSyncJobItemStatus.Pending;
                // Consecutive throttles with no progress in between (reset on every real item result).
                job.AttemptCount++;

                job.LastError = Sanitize(outcome.ErrorMessage ?? "Rate limited by TikTok API; pausing this job.");
                job.NextAttemptAt = now + ComputeRateLimitCooldown(job.AttemptCount, outcome.RetryAfterSeconds);

                if (HasExhaustedRateLimitBudget(job, now))
                {
                    // Hard bound reached: stop honestly instead of parking/resuming forever.
                    await AbandonForRateLimitAsync(job, ct);
                    return;
                }

                job.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);

                _logger.LogWarning(
                    "Batch sync job {JobId}: TikTok rate limited (consecutive pause {Attempt}/{Max}); parked until {Next:u} and released to the worker.",
                    job.Id, job.AttemptCount, Math.Max(1, _options.BatchSyncMaxRateLimitPauses), job.NextAttemptAt);
                return; // release the claim: the worker resumes the job once NextAttemptAt is due
            }

            if (outcome.Synced || outcome.NoEnrichment)
            {
                item.AttemptCount++;
                item.Status = TikTokBatchSyncJobItemStatus.Succeeded;
                item.ProcessedAt = now;
                item.LastError = outcome.NoEnrichment
                    ? "Details response contained no additional metric fields."
                    : null;
            }
            else if (outcome.NotResolvable)
            {
                item.AttemptCount++;
                item.Status = TikTokBatchSyncJobItemStatus.Failed;
                item.ProcessedAt = now;
                item.LastError = Sanitize(outcome.ErrorMessage ?? "Video could not be resolved for details sync.");
            }
            else
            {
                // Transient vs permanent failure: bounded per-item retries.
                item.AttemptCount++;
                item.ProcessedAt = now;
                item.LastError = Sanitize(outcome.ErrorMessage ?? "Details sync failed.");

                var kind = ClassifyOutcome(outcome);
                var maxAttempts = Math.Max(1, _options.BatchSyncMaxItemAttempts);
                if (kind == ErrorKind.Transient && item.AttemptCount < maxAttempts)
                {
                    item.Status = TikTokBatchSyncJobItemStatus.Pending;
                    transientRetry = true;
                }
                else
                {
                    item.Status = TikTokBatchSyncJobItemStatus.Failed;
                }
            }

            // Any real item result means TikTok is answering again, so the consecutive rate-limit
            // budget restarts. An intermittent 429 late in a long job can therefore no longer
            // accumulate towards the give-up bound.
            job.AttemptCount = 0;

            // Reclassification AFTER the latest detail data was persisted (or honestly left
            // untouched when no signal exists - see ContentTypeClassifier.TryClassify).
            await ClassifyContentLogAsync(log, ct);

            // Persist the item transition + ContentLog classification first, then recompute
            // counters from the persisted state (counters always mirror actual item states).
            await _db.SaveChangesAsync(ct);

            await RefreshCountersAsync(job, ct);
            job.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            if (transientRetry)
            {
                await Task.Delay(ComputeBackoff(item.AttemptCount), ct);
            }
        }

        await FinalizeJobAsync(job, ct);
    }

    /// <summary>
    /// Signal-based classification repair for one ContentLog (helpers from
    /// ContentTypeClassifier, rules untouched). Uses the most recent non-empty ProductsJson
    /// across metrics so a details snapshot without products cannot downgrade an existing
    /// KK classification. Persists to SQL Server; leaves ContentTypeId untouched (and
    /// reports it as unclassified in the status endpoint) when no signal exists.
    /// </summary>
    private async Task ClassifyContentLogAsync(ContentLog log, CancellationToken ct)
    {
        var productsJson = await _db.ContentMetrics.AsNoTracking()
            .Where(m => m.ContentLogId == log.Id && m.ProductsJson != null && m.ProductsJson != "")
            .OrderByDescending(m => m.CapturedAt)
            .Select(m => m.ProductsJson)
            .FirstOrDefaultAsync(ct);

        var typeId = ContentTypeClassifier.TryClassify(log.IsLive, log.HasCommerce, productsJson);
        if (typeId.HasValue && typeId != log.ContentTypeId)
        {
            var previous = log.ContentTypeId;
            log.ContentTypeId = typeId;
            log.UpdatedAt = DateTime.UtcNow;
            // Persisted by the SaveChanges that follows (the entity is tracked).
            _logger.LogInformation(
                "Batch sync: ContentLog {LogId} (VideoId={VideoId}) classified {Old} -> {New}.",
                log.Id, log.VideoId, previous, typeId);
        }
    }

    private async Task FinalizeJobAsync(TikTokBatchSyncJob job, CancellationToken ct)
    {
        await RefreshCountersAsync(job, ct);

        var notAttempted = await _db.TikTokBatchSyncJobItems.AsNoTracking()
            .CountAsync(i => i.TikTokBatchSyncJobId == job.Id
                && i.Status == TikTokBatchSyncJobItemStatus.NotAttempted, ct);

        var total = job.PendingItemsCount + job.ProcessingItemsCount
            + job.SucceededItemsCount + job.FailedItemsCount + notAttempted;

        // Failed (attempted and gave up) and NotAttempted (never tried) are reported separately so
        // an untried video is never presented as a failure.
        if (job.FailedItemsCount > 0 || notAttempted > 0)
        {
            job.LastError = await BuildFailureSummaryAsync(job, total, job.FailedItemsCount, notAttempted, ct);
            job.Status = job.SucceededItemsCount > 0
                ? TikTokBatchSyncJobStatus.CompletedWithErrors
                : TikTokBatchSyncJobStatus.Failed;
        }
        else
        {
            job.Status = TikTokBatchSyncJobStatus.Completed;
            job.LastError = null;
        }

        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        job.AttemptCount = 0;
        job.NextAttemptAt = null;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Batch sync job {JobId} finished: {Status}. Total={Total} Succeeded={Succeeded} Failed={Failed} NotAttempted={NotAttempted}.",
            job.Id, job.Status, total, job.SucceededItemsCount, job.FailedItemsCount, notAttempted);
    }

    /// <summary>
    /// Builds the job-level error summary from the DOMINANT cause across all unsuccessful items
    /// (grouped by message), instead of reporting whichever item happened to fail first. Keeps
    /// the attempted failures and the never-attempted items clearly separated.
    /// </summary>
    private async Task<string> BuildFailureSummaryAsync(
        TikTokBatchSyncJob job, int total, int failed, int notAttempted, CancellationToken ct)
    {
        var reasons = await _db.TikTokBatchSyncJobItems.AsNoTracking()
            .Where(i => i.TikTokBatchSyncJobId == job.Id
                && (i.Status == TikTokBatchSyncJobItemStatus.Failed
                    || i.Status == TikTokBatchSyncJobItemStatus.NotAttempted))
            .Select(i => i.LastError)
            .ToListAsync(ct);

        var dominant = reasons
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .GroupBy(r => r!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        var head = notAttempted == 0
            ? $"{failed} of {total} video(s) failed."
            : $"{failed + notAttempted} of {total} video(s) not synced ({failed} failed, {notAttempted} not attempted because the job stopped while TikTok was rate-limiting it).";

        var cause = dominant == null
            ? "Dominant cause: no error message recorded."
            : $"Dominant cause: {dominant.Count()} of {failed + notAttempted} unsuccessful item(s): {dominant.Key}";

        return Sanitize(head + " " + cause) ?? head;
    }

    /// <summary>
    /// Terminal stop for a job whose bounded rate-limit budget ran out: every still-open item is
    /// recorded as <see cref="TikTokBatchSyncJobItemStatus.NotAttempted"/> (never Failed, because
    /// it was never tried) and the job is finalized honestly.
    /// </summary>
    private async Task AbandonForRateLimitAsync(TikTokBatchSyncJob job, CancellationToken ct)
    {
        const string reason =
            "Not attempted: TikTok rate-limited this job and its bounded retry budget was exhausted. Start a new sync job later.";

        var untried = await _db.TikTokBatchSyncJobItems
            .Where(i => i.TikTokBatchSyncJobId == job.Id
                && (i.Status == TikTokBatchSyncJobItemStatus.Pending
                    || i.Status == TikTokBatchSyncJobItemStatus.Processing))
            .ToListAsync(ct);

        foreach (var item in untried)
        {
            item.Status = TikTokBatchSyncJobItemStatus.NotAttempted;
            item.LastError = reason;
        }

        // Persist item transitions first so the summary below reads real item states.
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Batch sync job {JobId}: bounded rate-limit budget exhausted after {Attempt} consecutive pause(s); {Count} item(s) left NotAttempted.",
            job.Id, job.AttemptCount, untried.Count);

        await FinalizeJobAsync(job, ct);
    }

    private async Task FailJobAsync(TikTokBatchSyncJob job, string message, CancellationToken ct)
    {
        job.Status = TikTokBatchSyncJobStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        job.LastError = Sanitize(message);
        job.NextAttemptAt = null;

        await FailRemainingItemsAsync(job, $"Job failed: {job.LastError}", ct, updateJob: false);

        _logger.LogError("Batch sync job {JobId} failed: {Message}", job.Id, job.LastError);
        await _db.SaveChangesAsync(ct);
    }

    private async Task FailRemainingItemsAsync(TikTokBatchSyncJob job, string reason, CancellationToken ct, bool updateJob = true)
    {
        var openItems = await _db.TikTokBatchSyncJobItems
            .Where(i => i.TikTokBatchSyncJobId == job.Id
                && (i.Status == TikTokBatchSyncJobItemStatus.Pending
                    || i.Status == TikTokBatchSyncJobItemStatus.Processing))
            .ToListAsync(ct);

        foreach (var item in openItems)
        {
            item.Status = TikTokBatchSyncJobItemStatus.Failed;
            item.LastError = Sanitize(reason);
            item.ProcessedAt = DateTime.UtcNow;
        }

        // Persist item transitions first so the counter query below reads current state.
        await _db.SaveChangesAsync(ct);

        await RefreshCountersAsync(job, ct);
        if (updateJob) job.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private async Task RefreshCountersAsync(TikTokBatchSyncJob job, CancellationToken ct)
    {
        var counts = await _db.TikTokBatchSyncJobItems
            .Where(i => i.TikTokBatchSyncJobId == job.Id)
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int CountOf(TikTokBatchSyncJobItemStatus status) =>
            counts.Where(c => c.Status == status).Sum(c => c.Count);

        job.PendingItemsCount = CountOf(TikTokBatchSyncJobItemStatus.Pending);
        job.ProcessingItemsCount = CountOf(TikTokBatchSyncJobItemStatus.Processing);
        job.SucceededItemsCount = CountOf(TikTokBatchSyncJobItemStatus.Succeeded);
        job.FailedItemsCount = CountOf(TikTokBatchSyncJobItemStatus.Failed);
        job.TotalVideoIdsFound = counts.Sum(c => c.Count);
    }

    // Retry behaviour per kind:
    //  - Permanent     -> job fails immediately (e.g. 401/403, missing credential/config).
    //  - Network       -> park the job with backoff (DNS resolution, connect/TLS failure before
    //                     any HTTP response, or a request timeout).
    //  - RateLimit     -> park the job with backoff (HTTP 429 / business code 36009002).
    //  - Transient     -> server responded with a recoverable error (e.g. 5xx): bounded in-process
    //                     retry, no parking.
    private enum ErrorKind { Transient, RateLimit, Network, Permanent }

    private ErrorKind ClassifyError(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode == HttpStatusCode.TooManyRequests) return ErrorKind.RateLimit;
            if (httpEx.StatusCode.HasValue)
            {
                var code = (int)httpEx.StatusCode.Value;
                if (code == 408) return ErrorKind.Network;               // request timeout
                if (code >= 400 && code < 500) return ErrorKind.Permanent;
                return ErrorKind.Transient;                               // 5xx etc: retry in-process
            }

            // StatusCode is null: the request failed before any HTTP response (DNS resolution,
            // TCP connect, TLS). We deliberately do NOT assume this is always DNS - see
            // GetDetailedErrorMessage, which keeps the message specific but not over-claiming.
            return ErrorKind.Network;
        }

        if (ex is TikTokVideoListException listEx)
            return listEx.Code == 36009002 ? ErrorKind.RateLimit : ErrorKind.Transient;

        if (ex is TikTokAuthException) return ErrorKind.Permanent;
        if (ex is InvalidOperationException) return ErrorKind.Permanent; // missing credential/config

        // Socket/timeout/connection exceptions that reached us unwrapped.
        if (IsNetworkFailure(ex)) return ErrorKind.Network;

        return ErrorKind.Transient;
    }

    /// <summary>
    /// Detect if an exception indicates DNS resolution or network connectivity failure
    /// (as opposed to HTTP response errors or business logic errors).
    /// </summary>
    private bool IsNetworkFailure(Exception ex)
    {
        if (ex == null) return false;

        // Timeouts surface as OperationCanceledException/TaskCanceledException (HttpClient
        // timeout) or TimeoutException. When this is reached the token is NOT cancelled (real
        // shutdown is filtered out by the callers), so it is a network-level timeout.
        if (ex is TimeoutException || ex is OperationCanceledException) return true;

        // Check if this is explicitly a DNS/network problem
        var message = ex.Message ?? "";
        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("name resolution", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Name resolution", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Temporary failure", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connection reset", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check inner exception chain for socket/network errors
        var inner = ex.InnerException;
        while (inner != null)
        {
            var innerMsg = inner.Message ?? "";
            if (innerMsg.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
                innerMsg.Contains("name resolution", StringComparison.OrdinalIgnoreCase) ||
                innerMsg.Contains("Name resolution", StringComparison.OrdinalIgnoreCase) ||
                innerMsg.Contains("Temporary failure", StringComparison.OrdinalIgnoreCase) ||
                innerMsg.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
                innerMsg.Contains("connection reset", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // On .NET, System.Net.Http.HttpRequestException may wrap a System.Net.SocketException or similar
            var innerTypeName = inner.GetType().FullName ?? "";
            if (innerTypeName.Contains("SocketException", StringComparison.OrdinalIgnoreCase) ||
                innerTypeName.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase) ||
                innerTypeName.Contains("OperationCanceledException", StringComparison.OrdinalIgnoreCase) ||
                innerTypeName.Contains("TaskCanceledException", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            inner = inner.InnerException;
        }

        return false;
    }

    private ErrorKind ClassifyOutcome(TikTokDetailsSyncService.SingleVideoSyncOutcome outcome)
    {
        if (outcome.Throttled) return ErrorKind.RateLimit;
        if (outcome.BusinessCode == 36009002) return ErrorKind.RateLimit;
        if (outcome.StatusCode.HasValue)
        {
            var code = outcome.StatusCode.Value;
            if (code == 408 || code == 429) return ErrorKind.RateLimit;
            if (code >= 400 && code < 500) return ErrorKind.Permanent;
        }
        // Unknown/business errors: retried up to the per-item attempt limit (bounded).
        return ErrorKind.Transient;
    }

    /// <summary>
    /// True when the job has already used up its consecutive-pause budget AND has been running for
    /// longer than the configured hard bound. Both conditions are required: a large job may be
    /// resumed many times, but it may not park/resume forever.
    /// </summary>
    private bool HasExhaustedRateLimitBudget(TikTokBatchSyncJob job, DateTime now)
    {
        if (job.AttemptCount < Math.Max(1, _options.BatchSyncMaxRateLimitPauses)) return false;

        var maxRun = TimeSpan.FromMinutes(Math.Max(1, _options.BatchSyncMaxRunMinutes));
        var startedAt = job.StartedAt ?? job.CreatedAt;
        return now - startedAt >= maxRun;
    }

    /// <summary>
    /// Cooldown before the job may be resumed: the larger of the bounded exponential backoff and a
    /// valid _Retry-After_ hint from TikTok, always capped by BatchSyncMaxCooldownSeconds.
    /// </summary>
    private TimeSpan ComputeRateLimitCooldown(int attempt, int? retryAfterSeconds)
    {
        var cap = TimeSpan.FromSeconds(Math.Max(1, _options.BatchSyncMaxCooldownSeconds));
        var backoff = ComputeBackoff(attempt);
        if (backoff > cap) backoff = cap;

        if (retryAfterSeconds.HasValue && retryAfterSeconds.Value > 0)
        {
            var hint = TimeSpan.FromSeconds(retryAfterSeconds.Value);
            if (hint > cap) hint = cap;
            return hint > backoff ? hint : backoff;
        }

        return backoff;
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseSeconds = Math.Max(0, _options.BatchSyncRetryBaseSeconds);
        var exponent = Math.Min(Math.Max(attempt - 1, 0), 8);
        // A parked retry must never be scheduled at "now": a zero delay would create a tight
        // loop and make NextAttemptAt meaningless. Floor at 1s (production base is 5s).
        var seconds = baseSeconds == 0 ? 1 : Math.Min(baseSeconds * (1 << exponent), 300);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task DelayUntilNextAttemptAsync(TikTokBatchSyncJob job, CancellationToken ct)
    {
        if (!job.NextAttemptAt.HasValue) return;
        var delay = job.NextAttemptAt.Value - DateTime.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
    }

    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Another worker won the compare-and-swap on Status.
            _logger.LogWarning("Batch sync: claim conflict, another worker won. {Message}", ex.Message);
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>Truncates and scrubs error text so no token/secret can leak into job errors or logs.</summary>
    internal static string? Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var cleaned = message;

        // Remove known credential markers and their typical values
        foreach (var marker in new[] { "access_token", "x-tts-access-token", "app_secret", "refresh_token", "sign=", "token:", "secret:", "key:" })
        {
            var idx = cleaned.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                // Remove marker and following ~50 chars (typical token length)
                var endIdx = Math.Min(idx + marker.Length + 50, cleaned.Length);
                cleaned = cleaned.Remove(idx, endIdx - idx);
                idx = cleaned.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Also remove long hexadecimal or base64-like strings that might be tokens (continuous alphanumeric of 32+ chars)
        var tokenPattern = System.Text.RegularExpressions.Regex.Matches(cleaned, @"[A-Za-z0-9_\-]{32,}");
        foreach (System.Text.RegularExpressions.Match match in System.Linq.Enumerable.Reverse(tokenPattern.Cast<System.Text.RegularExpressions.Match>()))
        {
            // Only remove if it looks like a token (no spaces, mostly alphanumeric)
            if (System.Text.RegularExpressions.Regex.IsMatch(match.Value, @"^[A-Za-z0-9_\-]+$"))
            {
                cleaned = cleaned.Remove(match.Index, match.Length);
            }
        }

        cleaned = cleaned.Replace('\r', ' ').Replace('\n', ' ');
        return cleaned.Length > 500 ? cleaned[..500] + "..." : cleaned;
    }
}
