using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

public class TikTokVideoService : ITikTokVideoService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TikTokOptions _options;
    private readonly AppDbContext _db;
    private readonly ITikTokSignatureService _signatureService;
    private readonly ITikTokAuthService _authService;
    private readonly Microsoft.Extensions.Logging.ILogger<TikTokVideoService> _logger;

    public TikTokVideoService(
        IHttpClientFactory httpFactory,
        Microsoft.Extensions.Options.IOptions<TikTokOptions> options,
        AppDbContext db,
        ITikTokAuthService authService,
        ITikTokSignatureService signatureService,
        Microsoft.Extensions.Logging.ILogger<TikTokVideoService>? logger = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _db = db;
        _authService = authService;
        _signatureService = signatureService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TikTokVideoService>.Instance;
    }

    public async Task<DryRunReport> DryRunVideoClassificationAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default)
    {
        var report = new DryRunReport();

        var credential = await _db.TikTokCredentials.FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (credential == null)
            throw new InvalidOperationException("No TikTok credential available. Exchange tokens first.");

        var accessToken = await _authService.GetValidAccessTokenAsync(credential.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("No valid access token available.");

        var client = _httpFactory.CreateClient("TikTokApi");
        var path = "/analytics/202605/shop_videos/performance";

        string? pageToken = null;

        var groundTruth = new HashSet<string>
        {
            "7683436081873800455",
            "7683467066388663559",
            "7683493477568515335",
            "7683510565985176840",
            "7683526456294640904",
            "7683563143968345364",
            "7683576265256783125"
        };

        do
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var query = new Dictionary<string, string?>
            {
                { "app_key", _options.AppKey },
                { "timestamp", timestamp },
                { "shop_cipher", shopCipher },
                { "page_size", "100" }
            };

            if (!string.IsNullOrWhiteSpace(pageToken)) query["page_token"] = pageToken;
            var effectiveStartDate = string.IsNullOrWhiteSpace(startDateIso) ? DateTime.UtcNow.Date.AddDays(-30) : DateTime.Parse(startDateIso).Date;
            var effectiveEndDate = string.IsNullOrWhiteSpace(endDateIso)
                ? DateTime.UtcNow.Date.AddDays(1)
                : DateTime.Parse(endDateIso).Date;

            query["start_date_ge"] = effectiveStartDate.ToString("yyyy-MM-dd");
            query["end_date_lt"] = effectiveEndDate.ToString("yyyy-MM-dd");

            var signInput = query.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var sign = _signatureService.GenerateSignature("GET", path, signInput, null, null);
            signInput.Add("sign", sign);

            var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(path, signInput);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-tts-access-token", accessToken);
            req.Headers.Add("Accept", "application/json");

            using var res = await client.SendAsync(req, cancellationToken);
            var content = await res.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("videos", out var videosElem) && videosElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in videosElem.EnumerateArray())
                {
                    report.Summary.TotalReceived++;

                    var videoId = v.GetPropertyOrDefault("id") ?? v.GetPropertyOrDefault("video_id");
                    if (string.IsNullOrWhiteSpace(videoId)) continue;

                    var entry = new DryRunVideoEntry { VideoId = videoId };

                    // find matching ContentLog by VideoId
                    var cl = await _db.ContentLogs.AsNoTracking().FirstOrDefaultAsync(x => x.VideoId == videoId, cancellationToken);
                    if (cl != null)
                    {
                        entry.ContentLogId = cl.Id;
                        entry.ExistingContentTypeId = cl.ContentTypeId;
                        report.Summary.TotalMatched++;
                    }
                    else
                    {
                        report.Summary.TotalUnmatched++;
                    }

                    entry.Title = v.GetPropertyOrDefault("title");
                    entry.Username = v.GetPropertyOrDefault("username");
                    var postTimeText = v.GetPropertyOrDefault("video_post_time");
                    if (!string.IsNullOrWhiteSpace(postTimeText) && DateTime.TryParse(postTimeText, out var parsed)) entry.VideoPostTime = parsed;

                    // author type from nested creator if present
                    if (v.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object)
                    {
                        entry.AuthorType = creator.GetPropertyOrDefault("author_type");
                    }

                    // products array
                    if (v.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
                    {
                        entry.ProductsAvailable = true;
                        entry.ProductsCount = products.GetArrayLength();
                        foreach (var p in products.EnumerateArray())
                        {
                            var name = p.GetPropertyOrDefault("name");
                            if (!string.IsNullOrWhiteSpace(name)) entry.ProductNames.Add(name);
                        }
                    }

                    // gmv
                    if (v.TryGetProperty("gmv", out var gmv) && gmv.ValueKind == JsonValueKind.Object)
                    {
                        var amount = gmv.GetPropertyOrDefault("amount");
                        if (decimal.TryParse(amount, out var damt)) entry.GmvAmount = damt;
                        entry.GmvCurrency = gmv.GetPropertyOrDefault("currency");
                        if (entry.GmvAmount.HasValue) report.Summary.WithGmvCount++;
                    }

                    // items_sold
                    entry.ItemsSold = v.GetPropertyOrDefaultInt("items_sold");
                    if (entry.ItemsSold.GetValueOrDefault() > 0) report.Summary.WithItemsSoldCount++;

                    // sku_orders
                    entry.SkuOrders = v.GetPropertyOrDefaultInt("sku_orders");
                    if (entry.SkuOrders.GetValueOrDefault() > 0) report.Summary.WithSkuOrdersCount++;

                    // hashtags
                    if (v.TryGetProperty("hash_tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tags.EnumerateArray())
                        {
                            entry.HashTags.Add(t.ToString());
                        }
                    }

                    // property names available on video object
                    foreach (var prop in v.EnumerateObject())
                    {
                        entry.PropertyNames.Add(prop.Name);
                    }

                    // counters
                    if (entry.ProductsAvailable) report.Summary.WithProductsCount++;
                    else report.Summary.WithoutProductsCount++;

                    report.Entries.Add(entry);

                    if (groundTruth.Contains(videoId))
                    {
                        report.GroundTruthMatches[videoId] = entry;
                    }
                }
            }

            // next page token
            string? nextPage = null;
            if (data.ValueKind == JsonValueKind.Object)
            {
                nextPage = data.GetPropertyOrDefault("page_token") ?? data.GetPropertyOrDefault("next_page_token");
                if (nextPage == null && data.TryGetProperty("page_info", out var pageInfo) && pageInfo.ValueKind == JsonValueKind.Object)
                {
                    nextPage = pageInfo.GetPropertyOrDefault("page_token") ?? pageInfo.GetPropertyOrDefault("next_page_token");
                }
            }

            pageToken = string.IsNullOrWhiteSpace(nextPage) ? null : nextPage;

        } while (!string.IsNullOrWhiteSpace(pageToken));

        return report;
    }

    public async Task<int> FetchAndSaveVideoListAsync(string shopCipher, string? startDateIso = null, string? endDateIso = null, CancellationToken cancellationToken = default)
    {
        // Validate credential
        var credential = await _db.TikTokCredentials.FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (credential == null)
            throw new InvalidOperationException("No TikTok credential available. Exchange tokens first.");

        var accessToken = await _authService.GetValidAccessTokenAsync(credential.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("No valid access token available.");

        var client = _httpFactory.CreateClient("TikTokApi");

        var path = "/analytics/202605/shop_videos/performance";

        var processed = 0;
        string? pageToken = null;

        do
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var query = new Dictionary<string, string?>
            {
                { "app_key", _options.AppKey },
                { "timestamp", timestamp },
                { "shop_cipher", shopCipher },
                { "page_size", "100" }
            };

            if (!string.IsNullOrWhiteSpace(pageToken)) query["page_token"] = pageToken;
            var effectiveStartDate = string.IsNullOrWhiteSpace(startDateIso) ? DateTime.UtcNow.Date.AddDays(-30) : DateTime.Parse(startDateIso).Date;

            var effectiveEndDate = string.IsNullOrWhiteSpace(endDateIso)
                ? DateTime.UtcNow.Date.AddDays(1)
                : DateTime.Parse(endDateIso).Date;

            query["start_date_ge"] = effectiveStartDate.ToString("yyyy-MM-dd");
            query["end_date_lt"] = effectiveEndDate.ToString("yyyy-MM-dd");

            // Remove nulls for signing
            var signInput = query.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);

            var sign = _signatureService.GenerateSignature("GET", path, signInput, null, null);
            signInput.Add("sign", sign);

            var url = QueryHelpers.AddQueryString(path, signInput);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-tts-access-token", accessToken);
            req.Headers.Add("Accept", "application/json");

            using var res = await client.SendAsync(req, cancellationToken);
            var status = res.StatusCode;
            var content = await res.Content.ReadAsStringAsync(cancellationToken); // BREAKPOINT DI SINI

            _logger.LogInformation(
                "TikTok video API response for shop {ShopCipher}: {Response}",
                shopCipher,
                content);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TikTok video list HTTP {Status} for shop {ShopCipher}. Response: {Response}",
                    status,
                    shopCipher,
                    content);

                throw new HttpRequestException(
                    $"TikTok video list HTTP {res.StatusCode}. Response: {content}");
            }

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeElem) && codeElem.TryGetInt32(out var codeVal) ? codeVal : 0;
            var requestId = root.GetPropertyOrDefault("request_id");
            var message = root.GetPropertyOrDefault("message");
            if (code != 0)
            {
                _logger.LogWarning("TikTok API returned code {Code} message {Message} request_id {RequestId} for shop {ShopCipher}", code, message, requestId, shopCipher);
                throw new InvalidOperationException($"TikTok video list error: {message ?? "(no message)"} (request_id={requestId})");
            }

            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;
            // process videos if present
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("videos", out var videosElem) && videosElem.ValueKind == JsonValueKind.Array)
            {
                _logger.LogDebug("TikTok returned {Count} videos for shop {ShopCipher}", videosElem.GetArrayLength(), shopCipher);
                // resolve shop id once per page
                var shop = await _db.TikTokShops.FirstOrDefaultAsync(x => x.ShopCipher == shopCipher, cancellationToken);
                long? shopId = shop?.Id;

                var toSave = new List<ContentLog>();
                var toUpdate = new List<ContentLog>();

                foreach (var v in videosElem.EnumerateArray())
                {
                    var videoId = v.GetPropertyOrDefault("id") ?? v.GetPropertyOrDefault("video_id");

                    if (string.IsNullOrWhiteSpace(videoId))
                        continue;

                    var title = v.GetPropertyOrDefault("title");
                    var username = v.GetPropertyOrDefault("username");

                    DateTime? postDate = null;

                    var postTimeText = v.GetPropertyOrDefault("video_post_time");

                    if (!string.IsNullOrWhiteSpace(postTimeText) &&
                        DateTime.TryParse(
                            postTimeText,
                            out var parsedPostDate))
                    {
                        postDate = parsedPostDate;
                    }

                    var duration =
                        v.TryGetProperty("duration", out var dur) &&
                        dur.TryGetInt32(out var durVal)
                            ? durVal
                            : (int?)null;

                    var creator =
                        v.TryGetProperty("creator", out var cr)
                            ? cr
                            : default;

                    var creatorOpenId =
                        creator.ValueKind == JsonValueKind.Object
                            ? creator.GetPropertyOrDefault("open_id")
                            : null;

                    var creatorUsername =
                        creator.ValueKind == JsonValueKind.Object
                            ? creator.GetPropertyOrDefault("user_name")
                            : null;

                    var creatorNickname =
                        creator.ValueKind == JsonValueKind.Object
                            ? creator.GetPropertyOrDefault("nick_name")
                            : null;

                    var authorType =
                        creator.ValueKind == JsonValueKind.Object
                            ? creator.GetPropertyOrDefault("author_type")
                            : null;

                    var existing = await _db.ContentLogs.FirstOrDefaultAsync(x => x.TikTokShopId == shopId && x.VideoId == videoId, cancellationToken);
                    if (existing == null)
                    {
                        var cl = new ContentLog
                        {
                            VideoId = videoId,
                            TikTokShopId = shopId,
                            VideoPostTime = postDate,
                            Title = title,
                            Username = username,
                            Duration = duration,
                            VideoUrl = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(videoId) ? $"https://www.tiktok.com/@{username}/video/{videoId}" : null,
                            CreatorOpenId = creatorOpenId,
                            CreatorUsername = creatorUsername,
                            CreatorNickname = creatorNickname,
                            AuthorType = authorType,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                            // default to Self Produce (seeded Id = 1)
                            ProductionMethodId = 1
                        };
                        // parse optional flags from TikTok response to help classification
                        var archivedStr = v.GetPropertyOrDefault("archived") ?? v.GetPropertyOrDefault("is_private") ?? v.GetPropertyOrDefault("privacy_status");
                        if (!string.IsNullOrWhiteSpace(archivedStr))
                        {
                            var low = archivedStr.Trim().ToLowerInvariant();
                            cl.IsArchived = low == "true" || low == "1" || low.Contains("archive") || low.Contains("private");
                        }

                        var hasCommerceStr = v.GetPropertyOrDefault("has_shopping_cart") ?? v.GetPropertyOrDefault("has_commerce") ?? v.GetPropertyOrDefault("commerce_info");
                        if (!string.IsNullOrWhiteSpace(hasCommerceStr))
                        {
                            var low2 = hasCommerceStr.Trim().ToLowerInvariant();
                            cl.HasCommerce = low2 == "true" || low2 == "1" || !string.IsNullOrEmpty(hasCommerceStr);
                        }
                        toSave.Add(cl);
                    }
                    else
                    {
                        // update only TikTok-owned fields, keep internal classification fields intact
                        existing.VideoPostTime = postDate;
                        existing.Title = title ?? existing.Title;
                        existing.Username = username ?? existing.Username;
                        existing.Duration = duration ?? existing.Duration;
                        existing.VideoUrl = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(videoId) ? $"https://www.tiktok.com/@{username}/video/{videoId}" : existing.VideoUrl;
                        existing.CreatorOpenId = creatorOpenId ?? existing.CreatorOpenId;
                        existing.CreatorUsername = creatorUsername ?? existing.CreatorUsername;
                        existing.CreatorNickname = creatorNickname ?? existing.CreatorNickname;
                        existing.AuthorType = authorType ?? existing.AuthorType;
                        existing.UpdatedAt = DateTime.UtcNow;
                        // update TikTok-derived signals
                        var archivedStr = v.GetPropertyOrDefault("archived") ?? v.GetPropertyOrDefault("is_private") ?? v.GetPropertyOrDefault("privacy_status");
                        if (!string.IsNullOrWhiteSpace(archivedStr))
                        {
                            var low = archivedStr.Trim().ToLowerInvariant();
                            existing.IsArchived = low == "true" || low == "1" || low.Contains("archive") || low.Contains("private");
                        }

                        var hasCommerceStr = v.GetPropertyOrDefault("has_shopping_cart") ?? v.GetPropertyOrDefault("has_commerce") ?? v.GetPropertyOrDefault("commerce_info");
                        if (!string.IsNullOrWhiteSpace(hasCommerceStr))
                        {
                            var low2 = hasCommerceStr.Trim().ToLowerInvariant();
                            existing.HasCommerce = low2 == "true" || low2 == "1" || !string.IsNullOrEmpty(hasCommerceStr);
                        }
                        toUpdate.Add(existing);
                    }
                }

                if (toSave.Count > 0)
                {
                    _db.ContentLogs.AddRange(toSave);
                    processed += toSave.Count;
                }

                if (toUpdate.Count > 0)
                {
                    processed += toUpdate.Count;
                }

                if (toSave.Count > 0 || toUpdate.Count > 0)
                {
                    _logger.LogInformation(
                        "Saving TikTok video page for shop {ShopCipher}. " +
                        "ToSave={ToSaveCount}, ToUpdate={ToUpdateCount}",
                        shopCipher,
                        toSave.Count,
                        toUpdate.Count);

                    try
                    {
                        await _db.SaveChangesAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Failed saving TikTok videos for shop {ShopCipher}. " +
                            "ToSave={ToSaveCount}, ToUpdate={ToUpdateCount}",
                            shopCipher,
                            toSave.Count,
                            toUpdate.Count);

                        throw;
                    }
                }
            }

            // determine next page token - support common variants
            string? nextPage = null;
            if (data.ValueKind == JsonValueKind.Object)
            {
                nextPage = data.GetPropertyOrDefault("page_token") ?? data.GetPropertyOrDefault("next_page_token");
                // also support a nested page_info object
                if (nextPage == null && data.TryGetProperty("page_info", out var pageInfo) && pageInfo.ValueKind == JsonValueKind.Object)
                {
                    nextPage = pageInfo.GetPropertyOrDefault("page_token") ?? pageInfo.GetPropertyOrDefault("next_page_token");
                }
            }

            pageToken = string.IsNullOrWhiteSpace(nextPage) ? null : nextPage;

        } while (!string.IsNullOrWhiteSpace(pageToken));

        return processed;
    }
}
