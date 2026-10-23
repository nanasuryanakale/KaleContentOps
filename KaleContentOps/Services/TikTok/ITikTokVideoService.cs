using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

/// <summary>
/// Outcome of ONE batch_performance request carrying MANY video ids
/// (GET /analytics/202609/shop_videos/batch_performance).
/// StatusCode is the HTTP status (0 = never sent / credential problem).
/// Code/Message/RequestId mirror the TikTok envelope; Code null or 0 = success.
/// VideoEntries = data.videos[] entries VERBATIM (each {video_id, video, performance}),
/// cloned out of the parsed document so callers can read them after this call returns.
/// </summary>
public sealed record TikTokBatchPerformanceResult(
    int StatusCode,
    int? Code,
    string? Message,
    string? RequestId,
    System.Collections.Generic.IReadOnlyList<System.Text.Json.JsonElement> VideoEntries);

public interface ITikTokVideoService
{
    /// <summary>
    /// Fetches video performance list for a shop (by shop cipher) and upserts ContentLog records.
    /// Returns the number of content logs created or updated.
    /// </summary>
    Task<int> FetchAndSaveVideoListAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches ONE page of the video performance list (all pagination params applied) and
    /// returns the parsed page without touching the database. Used by the persistent batch
    /// sync job so it can checkpoint page_token between pages and survive restarts.
    /// Throws HttpRequestException on non-2xx responses and TikTokVideoListException when
    /// the business code != 0 (identical semantics to FetchAndSaveVideoListAsync).
    /// </summary>
    Task<TikTokVideoListPage> FetchVideoListPageAsync(string shopCipher, string? startDateIso, string? endDateIso, string? pageToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists one page produced by <see cref="FetchVideoListPageAsync"/> through the SAME
    /// pipeline as the classic list sync: ContentLog insert/update keyed by (shop, video_id),
    /// list-phase ContentMetric, and signal parsing/classification. Idempotent per page.
    /// Returns the number of ContentLogs created or updated.
    /// </summary>
    Task<int> ApplyVideoListPageAsync(string shopCipher, TikTokVideoListPage page, CancellationToken cancellationToken = default);

    // Dry-run audit method: does not modify database. Returns a report of parsed videos and matches.
    Task<KaleContentOps.Services.TikTok.DryRunReport> DryRunVideoClassificationAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default);

    // Development-only diagnostic to perform a single performance API request and return status + parsed JSON
    Task<(int? StatusCode, string Endpoint, System.Text.Json.JsonElement? Root, System.Text.Json.JsonElement? Matched)> RunPerformanceDiagnosticAsync(string videoId, string shopCipher, CancellationToken cancellationToken = default);

    // Development-only diagnostic to call the official v202509 details endpoint for a single video.
    // RetryAfterSeconds carries a valid _Retry-After_ response header when TikTok sent one, so
    // callers can honor the server's own cooldown hint instead of guessing one.
    Task<(int? StatusCode, string Endpoint, System.Text.Json.JsonElement? Root, System.Text.Json.JsonElement? Data, int? RetryAfterSeconds)> RunDetailsDiagnosticAsync(
        string videoId,
        string shopCipher,
        CancellationToken cancellationToken = default,
        DateTime? startDateInclusive = null,
        DateTime? endDateExclusive = null);

    /// <summary>
    /// Fetches shop-video performance for MANY videos in ONE request
    /// (GET /analytics/202609/shop_videos/batch_performance: video_ids=comma-separated,
    /// optional granularity=1D for daily intervals). Verified live 2026-10-22, code=0:
    /// granularity=1D returns per-video daily intervals whose traffic object carries
    /// likes/comments/shares/views.
    ///
    /// Contract: business errors are NOT thrown and NOT retried here - Code/Message
    /// carry them and VideoEntries is empty; HTTP-level transport goes through the
    /// shared TikTokGetWithRetryAsync path (Retry-After, per-shop suspension) and the
    /// details semaphore so this call never outpaces the existing sync traffic.
    /// </summary>
    Task<TikTokBatchPerformanceResult> FetchBatchPerformanceAsync(
        System.Collections.Generic.IReadOnlyList<string> videoIds,
        string shopCipher,
        DateTime startDateInclusive,
        DateTime endDateExclusive,
        string? granularity,
        CancellationToken cancellationToken = default);
}
