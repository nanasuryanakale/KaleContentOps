using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

public interface ITikTokVideoService
{
    /// <summary>
    /// Fetches video performance list for a shop (by shop cipher) and upserts ContentLog records.
    /// Returns the number of content logs created or updated.
    /// </summary>
    Task<int> FetchAndSaveVideoListAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default);

    // Dry-run audit method: does not modify database. Returns a report of parsed videos and matches.
    Task<KaleContentOps.Services.TikTok.DryRunReport> DryRunVideoClassificationAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default);

    // Development-only diagnostic to perform a single performance API request and return status + parsed JSON
    Task<(int? StatusCode, string Endpoint, System.Text.Json.JsonElement? Root, System.Text.Json.JsonElement? Matched)> RunPerformanceDiagnosticAsync(string videoId, string shopCipher, CancellationToken cancellationToken = default);

    // Development-only diagnostic to call the official v202509 details endpoint for a single video
    Task<(int? StatusCode, string Endpoint, System.Text.Json.JsonElement? Root, System.Text.Json.JsonElement? Data)> RunDetailsDiagnosticAsync(string videoId, string shopCipher, CancellationToken cancellationToken = default);
}
