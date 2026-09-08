using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using KaleContentOps.Data;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

public class TikTokShopService : ITikTokShopService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TikTokOptions _options;
    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ITikTokSignatureService _signatureService;

    public TikTokShopService(
        IHttpClientFactory httpFactory,
        Microsoft.Extensions.Options.IOptions<TikTokOptions> options,
        AppDbContext db,
        IDataProtectionProvider dataProtection,
        ITikTokSignatureService signatureService)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _db = db;
        _protector = dataProtection.CreateProtector("TikTokAuthService.v1");
        _signatureService = signatureService;
    }

    public async Task<IList<TikTokShop>?> FetchAndSaveAuthorizedShopsAsync(CancellationToken cancellationToken = default)
    {
        // Load credential
        var credential = await _db.TikTokCredentials.FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (credential == null || string.IsNullOrEmpty(credential.EncryptedAccessToken))
            throw new InvalidOperationException("No TikTok credential available. Exchange tokens first.");

        var accessToken = _protector.Unprotect(credential.EncryptedAccessToken);

        var client = _httpFactory.CreateClient("TikTokApi");

        var path = "/authorization/202309/shops";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var query = new Dictionary<string, string?>
        {
            { "app_key", _options.AppKey },
            { "timestamp", timestamp }
        };

        // Sign requires exact algorithm — delegated to signature service (TODO: implement signature algorithm)
        var sign = _signatureService.GenerateSignature("GET", path, query, null, null);
        query.Add("sign", sign);

        var url = QueryHelpers.AddQueryString(path, query);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("x-tts-access-token", accessToken);
        req.Headers.Add("Accept", "application/json");

        using var res = await client.SendAsync(req, cancellationToken);
        var content = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Fetch authorized shops failed: {res.StatusCode}");
        }

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

        var shops = new List<TikTokShop>();

        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("shops", out var shopsElem) && shopsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in shopsElem.EnumerateArray())
            {
                // Extract fields cautiously; field names must be verified
                var shopCipher = item.GetPropertyOrDefault("shop_cipher");
                var shopId = item.GetPropertyOrDefault("shop_id");
                var shopCode = item.GetPropertyOrDefault("shop_code");
                var shopName = item.GetPropertyOrDefault("shop_name");
                var region = item.GetPropertyOrDefault("region");
                var sellerType = item.GetPropertyOrDefault("seller_type");

                var existing = await _db.TikTokShops.FirstOrDefaultAsync(x => x.ShopCipher == shopCipher, cancellationToken);
                if (existing == null)
                {
                    existing = new TikTokShop
                    {
                        ShopCipher = shopCipher,
                        ShopId = shopId,
                        ShopCode = shopCode,
                        ShopName = shopName,
                        Region = region,
                        SellerType = sellerType,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _db.TikTokShops.Add(existing);
                }
                else
                {
                    existing.ShopId = shopId ?? existing.ShopId;
                    existing.ShopCode = shopCode ?? existing.ShopCode;
                    existing.ShopName = shopName ?? existing.ShopName;
                    existing.Region = region ?? existing.Region;
                    existing.SellerType = sellerType ?? existing.SellerType;
                    existing.UpdatedAt = DateTime.UtcNow;
                }

                shops.Add(existing);
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // Data shape unexpected - mark for verification
            // TODO: verify response schema and update parsing accordingly
        }

        return shops;
    }
}
