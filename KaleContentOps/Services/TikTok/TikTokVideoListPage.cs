using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KaleContentOps.Services.TikTok;

/// <summary>
/// One page of the TikTok Shop Video Performance List API
/// (GET /analytics/202605/shop_videos/performance).
///
/// Produced by <see cref="ITikTokVideoService.FetchVideoListPageAsync"/> so callers can
/// paginate one page at a time (persistent job checkpointing), then persist the page with
/// <see cref="ITikTokVideoService.ApplyVideoListPageAsync"/> which runs the SAME
/// upsert/classification pipeline used by the classic full-list sync.
/// </summary>
public sealed class TikTokVideoListPage
{
    /// <summary>Shop cipher the page was fetched for.</summary>
    public string ShopCipher { get; init; } = string.Empty;

    /// <summary>Effective start_date_ge (inclusive) used for this page.</summary>
    public DateTime StartDate { get; init; }

    /// <summary>Effective end_date_lt (EXCLUSIVE) used for this page.</summary>
    public DateTime EndDate { get; init; }

    /// <summary>
    /// Raw video objects of this page (cloned, independent of the parsed JSON document).
    /// Empty when the response contains no <c>data.videos</c> array.
    /// </summary>
    public IReadOnlyList<JsonElement> Videos { get; init; } = Array.Empty<JsonElement>();

    /// <summary>
    /// Video ids of this page in response order (blank ids skipped). Ready to persist
    /// as job queue items without a second parser.
    /// </summary>
    public IReadOnlyList<string> VideoIds { get; init; } = Array.Empty<string>();

    /// <summary>Next pagination token, or null when this was the last page.</summary>
    public string? NextPageToken { get; init; }
}

/// <summary>
/// Business-level error (TikTok <c>code != 0</c>) from the video list API.
/// Derives from <see cref="InvalidOperationException"/> so existing callers/catch blocks
/// and tests that expect InvalidOperationException keep working unchanged; the message
/// text is identical to the pre-existing one.
/// </summary>
public class TikTokVideoListException : InvalidOperationException
{
    /// <summary>The TikTok business error code (e.g. 36009002 = rate limited).</summary>
    public int Code { get; }

    public TikTokVideoListException(string message, int code) : base(message)
    {
        Code = code;
    }
}
