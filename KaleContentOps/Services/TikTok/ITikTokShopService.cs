using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

public interface ITikTokShopService
{
    Task<IList<TikTokShop>?> FetchAndSaveAuthorizedShopsAsync(CancellationToken cancellationToken = default);
}
