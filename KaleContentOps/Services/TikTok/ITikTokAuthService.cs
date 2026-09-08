using System.Threading.Tasks;
using System.Threading;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

public interface ITikTokAuthService
{
    Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default);

    Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default);
}
