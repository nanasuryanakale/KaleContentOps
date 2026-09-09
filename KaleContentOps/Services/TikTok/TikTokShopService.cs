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
    private readonly ITikTokSignatureService _signatureService;
    private readonly ITikTokAuthService _authService;
    private readonly Microsoft.Extensions.Logging.ILogger<TikTokShopService> _logger;

    public TikTokShopService(
        IHttpClientFactory httpFactory,
        Microsoft.Extensions.Options.IOptions<TikTokOptions> options,
        AppDbContext db,
        ITikTokAuthService authService,
        ITikTokSignatureService signatureService,
        Microsoft.Extensions.Logging.ILogger<TikTokShopService>? logger = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _db = db;
        _authService = authService;
        _signatureService = signatureService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TikTokShopService>.Instance;
    }

    public async Task<IList<TikTokShop>?> FetchAndSaveAuthorizedShopsAsync(CancellationToken cancellationToken = default)
    {
        // Load credential record
        var credential = await _db.TikTokCredentials.FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (credential == null)
            throw new InvalidOperationException("No TikTok credential available. Exchange tokens first.");

        // Obtain a valid access token via the auth service (may refresh if needed)
        var accessToken = await _authService.GetValidAccessTokenAsync(credential.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("No valid access token available. Exchange tokens first.");

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

        // Validate top-level code
        var code = root.TryGetProperty("code", out var codeElem) && codeElem.TryGetInt32(out var codeVal) ? codeVal : 0;
        var requestId = root.GetPropertyOrDefault("request_id");
        if (code != 0)
        {
            var message = root.GetPropertyOrDefault("message") ?? "TikTok fetch authorized shops failed";
            throw new TikTokAuthException($"TikTok shops error: {message}", code, requestId);
        }

        var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

        var shops = new List<TikTokShop>();

        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("shops", out var shopsElem) && shopsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in shopsElem.EnumerateArray())
            {
                // Extract fields cautiously; accept multiple possible field names from API variants
                var shopCipher = item.GetPropertyOrDefault("shop_cipher") ?? item.GetPropertyOrDefault("cipher");
                var shopId = item.GetPropertyOrDefault("shop_id") ?? item.GetPropertyOrDefault("id");
                var shopCode = item.GetPropertyOrDefault("shop_code") ?? item.GetPropertyOrDefault("code");
                var shopName = item.GetPropertyOrDefault("shop_name") ?? item.GetPropertyOrDefault("name");
                var region = item.GetPropertyOrDefault("region");
                var sellerType = item.GetPropertyOrDefault("seller_type") ?? item.GetPropertyOrDefault("user_type");

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
            _logger.LogInformation("Fetched and saved {Count} authorized TikTok shops", shops.Count);
        }
        else
        {
            // Data shape unexpected - mark for verification
            // TODO: verify response schema and update parsing accordingly
        }

        return shops;
    }
}
