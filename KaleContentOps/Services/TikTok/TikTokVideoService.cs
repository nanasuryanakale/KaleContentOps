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
            if (!string.IsNullOrWhiteSpace(startDateIso)) query["start_date_ge"] = startDateIso;
            if (!string.IsNullOrWhiteSpace(endDateIso)) query["end_date_lt"] = endDateIso;

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
            var content = await res.Content.ReadAsStringAsync(cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("TikTok video list HTTP {Status} for shop {ShopCipher}", status, shopCipher);
                throw new HttpRequestException($"TikTok video list HTTP {res.StatusCode}");
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
                    var videoId = v.GetPropertyOrDefault("video_id");
                    if (string.IsNullOrWhiteSpace(videoId)) continue;

                    var title = v.GetPropertyOrDefault("title");
                    var username = v.GetPropertyOrDefault("username");
                    var postTime = v.GetPropertyOrDefaultLong("video_post_time");
                    DateTime? postDate = postTime.HasValue ? DateTimeOffset.FromUnixTimeSeconds(postTime.Value).UtcDateTime : (DateTime?)null;
                    var duration = v.TryGetProperty("duration", out var dur) && dur.TryGetInt32(out var durVal) ? durVal : (int?)null;
                    var creator = v.TryGetProperty("creator", out var cr) ? cr : default;
                    var creatorOpenId = creator.ValueKind == JsonValueKind.Object ? creator.GetPropertyOrDefault("open_id") : null;
                    var creatorUsername = creator.ValueKind == JsonValueKind.Object ? creator.GetPropertyOrDefault("user_name") : null;
                    var creatorNickname = creator.ValueKind == JsonValueKind.Object ? creator.GetPropertyOrDefault("nick_name") : null;
                    var authorType = creator.ValueKind == JsonValueKind.Object ? creator.GetPropertyOrDefault("author_type") : null;

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
                            UpdatedAt = DateTime.UtcNow
                        };
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
                    await _db.SaveChangesAsync(cancellationToken);
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
