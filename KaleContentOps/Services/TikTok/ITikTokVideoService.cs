using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

public interface ITikTokVideoService
{
    /// <summary>
    /// Fetches video performance list for a shop (by shop cipher) and upserts ContentLog records.
    /// Returns the number of content logs created or updated.
    /// </summary>
    Task<int> FetchAndSaveVideoListAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default);
}
