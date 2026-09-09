using System.Threading.Tasks;
using System.Threading;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

public interface ITikTokAuthService
{
    Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default);

    Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default);

    Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a valid (not expired) access token for the given credential id. May refresh token if needed.
    /// </summary>
    Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default);
}
