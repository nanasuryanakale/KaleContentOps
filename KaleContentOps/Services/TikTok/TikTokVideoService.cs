using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;
using System.Linq;

namespace KaleContentOps.Services.TikTok;

public class TikTokVideoService : ITikTokVideoService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TikTokOptions _options;
    private readonly AppDbContext _db;
    private readonly ITikTokSignatureService _signatureService;
    private readonly ITikTokAuthService _authService;
    private readonly Microsoft.Extensions.Logging.ILogger<TikTokVideoService> _logger;
    private readonly Microsoft.Extensions.Hosting.IHostEnvironment? _env;

    public TikTokVideoService(
        IHttpClientFactory httpFactory,
        Microsoft.Extensions.Options.IOptions<TikTokOptions> options,
        AppDbContext db,
        ITikTokAuthService authService,
        ITikTokSignatureService signatureService,
        Microsoft.Extensions.Hosting.IHostEnvironment? env = null,
        Microsoft.Extensions.Logging.ILogger<TikTokVideoService>? logger = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _db = db;
        _authService = authService;
        _signatureService = signatureService;
        _env = env;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TikTokVideoService>.Instance;
    }

    // Parse v202509 details response 'data' element and extract metrics from performance.intervals[].traffic
    public DetailsMetrics ParseDetailsMetrics(JsonElement data)
    {
        var result = new DetailsMetrics();

        try
        {
            // navigate to data.performance.intervals[].traffic
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("performance", out var perf) && perf.ValueKind == JsonValueKind.Object && perf.TryGetProperty("intervals", out var intervals) && intervals.ValueKind == JsonValueKind.Array)
            {
                long viewsSum = 0;
                long likesSum = 0;
                long commentsSum = 0;
                long sharesSum = 0;
                long newFollowersSum = 0;

                decimal avgWatchSum = 0;
                bool avgWatchFound = false;
                decimal fullWatchSum = 0;
                bool fullWatchFound = false;
                long reachSum = 0;
                bool reachFound = false;

                foreach (var interval in intervals.EnumerateArray())
                {
                    if (interval.ValueKind != JsonValueKind.Object) continue;
                    if (!interval.TryGetProperty("traffic", out var traffic) || traffic.ValueKind != JsonValueKind.Object) continue;

                    // helper to read long
                    long? ReadLong(JsonElement elem, params string[] names)
                    {
                        foreach (var name in names)
                        {
                            if (elem.TryGetProperty(name, out var pe))
                            {
                                if (pe.ValueKind == JsonValueKind.Number && pe.TryGetInt64(out var lv)) return lv;
                                if (pe.ValueKind == JsonValueKind.String && long.TryParse(pe.GetString(), out var lv2)) return lv2;
                            }
                        }
                        return null;
                    }

                    decimal? ReadDecimal(JsonElement elem, params string[] names)
                    {
                        foreach (var name in names)
                        {
                            if (elem.TryGetProperty(name, out var pe))
                            {
                                if (pe.ValueKind == JsonValueKind.Number && pe.TryGetDecimal(out var dv)) return dv;
                                if (pe.ValueKind == JsonValueKind.String && decimal.TryParse(pe.GetString(), out var dv2)) return dv2;
                            }
                        }
                        return null;
                    }

                    var v = ReadLong(traffic, "views", "view_count"); if (v.HasValue) { viewsSum += v.Value; if (result.ViewsPath == null) result.ViewsPath = "data.performance.intervals[].traffic.views"; }
                    var l = ReadLong(traffic, "likes", "like_count"); if (l.HasValue) { likesSum += l.Value; if (result.LikesPath == null) result.LikesPath = "data.performance.intervals[].traffic.likes"; }
                    var c = ReadLong(traffic, "comments", "comment_count"); if (c.HasValue) { commentsSum += c.Value; if (result.CommentsPath == null) result.CommentsPath = "data.performance.intervals[].traffic.comments"; }
                    var s = ReadLong(traffic, "shares", "share_count"); if (s.HasValue) { sharesSum += s.Value; if (result.SharesPath == null) result.SharesPath = "data.performance.intervals[].traffic.shares"; }
                    var nf = ReadLong(traffic, "new_followers", "new_follower_count"); if (nf.HasValue) { newFollowersSum += nf.Value; if (result.NewFollowersPath == null) result.NewFollowersPath = "data.performance.intervals[].traffic.new_followers"; }

                    var aw = ReadDecimal(traffic, "average_watch_time", "avg_watch_time_ms"); if (aw.HasValue) { avgWatchFound = true; avgWatchSum += aw.Value; if (result.AverageWatchPath == null) result.AverageWatchPath = "data.performance.intervals[].traffic.average_watch_time"; }
                    var fr = ReadDecimal(traffic, "full_watch_rate", "finish_rate"); if (fr.HasValue) { fullWatchFound = true; fullWatchSum += fr.Value; if (result.FullWatchRatePath == null) result.FullWatchRatePath = "data.performance.intervals[].traffic.full_watch_rate"; }
                    var r = ReadLong(traffic, "reach", "reach_count"); if (r.HasValue) { reachFound = true; reachSum += r.Value; if (result.ReachPath == null) result.ReachPath = "data.performance.intervals[].traffic.reach"; }
                }

                // Assign sums even when zero to preserve explicit zero values from the API. Only leave null when no data present at all.
                result.Views = viewsSum;
                result.Likes = likesSum;
                result.Comments = commentsSum;
                result.Shares = sharesSum;
                result.NewFollowers = newFollowersSum;

                if (avgWatchFound) result.AverageWatch = avgWatchSum; // sum of avg may not be meaningful; preserve presence
                if (fullWatchFound) result.FullWatchRate = fullWatchSum;
                if (reachFound) result.Reach = reachSum;
                // Parse viewer_profile[type=="VIEWERS"] demographics from performance.viewer_profile if present
                try
                {
                    if (perf.ValueKind == JsonValueKind.Object && perf.TryGetProperty("viewer_profile", out var vp) && vp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var profile in vp.EnumerateArray())
                        {
                            if (profile.ValueKind != JsonValueKind.Object) continue;
                            if (!profile.TryGetProperty("type", out var ptype) || ptype.ValueKind != JsonValueKind.String) continue;
                            var t = ptype.GetString();
                            if (!string.Equals(t, "VIEWERS", System.StringComparison.OrdinalIgnoreCase)) continue;

                            // gender_distribution[]
                            if (profile.TryGetProperty("gender_distribution", out var gdist) && gdist.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var g in gdist.EnumerateArray())
                                {
                                    if (g.ValueKind != JsonValueKind.Object) continue;
                                    var gender = g.TryGetProperty("gender", out var ggender) && ggender.ValueKind == JsonValueKind.String ? ggender.GetString() : null;
                                    decimal? perc = null;
                                    if (g.TryGetProperty("percentage", out var gperc))
                                    {
                                        if (gperc.ValueKind == JsonValueKind.String && decimal.TryParse(gperc.GetString(), out var dperc)) perc = dperc;
                                        else if (gperc.ValueKind == JsonValueKind.Number && gperc.TryGetDecimal(out var dnum)) perc = dnum;
                                    }
                                    if (gender == null) continue;
                                    if (string.Equals(gender, "male", System.StringComparison.OrdinalIgnoreCase)) { result.Male = perc; if (result.MalePath == null) result.MalePath = "data.performance.viewer_profile[type==\"VIEWERS\"].gender_distribution[gender==\"male\"].percentage"; }
                                    else if (string.Equals(gender, "female", System.StringComparison.OrdinalIgnoreCase)) { result.Female = perc; if (result.FemalePath == null) result.FemalePath = "data.performance.viewer_profile[type==\"VIEWERS\"].gender_distribution[gender==\"female\"].percentage"; }
                                    else if (string.Equals(gender, "no_gender", System.StringComparison.OrdinalIgnoreCase) || string.Equals(gender, "unknown", System.StringComparison.OrdinalIgnoreCase)) { result.NoGender = perc; if (result.NoGenderPath == null) result.NoGenderPath = "data.performance.viewer_profile[type==\"VIEWERS\"].gender_distribution[gender==\"no_gender\"].percentage"; }
                                }
                            }

                            // age_distribution[]
                            if (profile.TryGetProperty("age_distribution", out var adist) && adist.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var a in adist.EnumerateArray())
                                {
                                    if (a.ValueKind != JsonValueKind.Object) continue;
                                    var ageRange = a.TryGetProperty("age", out var aage) && aage.ValueKind == JsonValueKind.String ? aage.GetString() : null;
                                    decimal? perc = null;
                                    if (a.TryGetProperty("percentage", out var aperc))
                                    {
                                        if (aperc.ValueKind == JsonValueKind.String && decimal.TryParse(aperc.GetString(), out var dperc)) perc = dperc;
                                        else if (aperc.ValueKind == JsonValueKind.Number && aperc.TryGetDecimal(out var dnum)) perc = dnum;
                                    }
                                    if (ageRange == null) continue;
                                    if (ageRange == "18-24") { result.Age18 = perc; if (result.Age18Path == null) result.Age18Path = "data.performance.viewer_profile[type==\"VIEWERS\"].age_distribution[age==\"18-24\"].percentage"; }
                                    else if (ageRange == "25-34") { result.Age25 = perc; if (result.Age25Path == null) result.Age25Path = "data.performance.viewer_profile[type==\"VIEWERS\"].age_distribution[age==\"25-34\"].percentage"; }
                                    else if (ageRange == "35-44") { result.Age35 = perc; if (result.Age35Path == null) result.Age35Path = "data.performance.viewer_profile[type==\"VIEWERS\"].age_distribution[age==\"35-44\"].percentage"; }
                                    else if (ageRange == "45-54") { result.Age45 = perc; if (result.Age45Path == null) result.Age45Path = "data.performance.viewer_profile[type==\"VIEWERS\"].age_distribution[age==\"45-54\"].percentage"; }
                                    else if (ageRange == "55+" || string.Equals(ageRange, "55 and above", System.StringComparison.OrdinalIgnoreCase)) { result.Age55 = perc; if (result.Age55Path == null) result.Age55Path = "data.performance.viewer_profile[type==\"VIEWERS\"].age_distribution[age==\"55+\"].percentage"; }
                                }
                            }

                            // stop after first VIEWERS profile
                            break;
                        }
                    }
                }
                catch
                {
                    // ignore parsing issues for demographics
                }
            }
        }
        catch
        {
            // parsing failures will leave fields null
        }

        return result;
    }

    // Map parsed DetailsMetrics into a ContentMetric entity (does not save to DB).
    // Caller should set ContentLogId and persist the returned entity when appropriate.
    public ContentMetric? MapDetailsMetricsToContentMetric(DetailsMetrics dm, long contentLogId)
    {
        if (dm == null) return null;

        var metric = new ContentMetric
        {
            ContentLogId = contentLogId,
            Views = dm.Views,
            Likes = dm.Likes,
            Comments = dm.Comments,
            Shares = dm.Shares,
            NewFollowers = dm.NewFollowers,
            Reach = dm.Reach,
            AverageWatch = dm.AverageWatch,
            FullWatchRate = dm.FullWatchRate,
            CapturedAt = DateTime.UtcNow
        };

        // Build demographics JSON only when any demographic value is present
        var demoObj = new Dictionary<string, object?>();
        bool anyDemo = false;
        if (dm.Male.HasValue) { demoObj["male"] = dm.Male.Value; anyDemo = true; }
        if (dm.Female.HasValue) { demoObj["female"] = dm.Female.Value; anyDemo = true; }
        if (dm.NoGender.HasValue) { demoObj["no_gender"] = dm.NoGender.Value; anyDemo = true; }

        var ages = new Dictionary<string, decimal?>();
        if (dm.Age18.HasValue) { ages["18-24"] = dm.Age18.Value; anyDemo = true; }
        if (dm.Age25.HasValue) { ages["25-34"] = dm.Age25.Value; anyDemo = true; }
        if (dm.Age35.HasValue) { ages["35-44"] = dm.Age35.Value; anyDemo = true; }
        if (dm.Age45.HasValue) { ages["45-54"] = dm.Age45.Value; anyDemo = true; }
        if (dm.Age55.HasValue) { ages["55+"] = dm.Age55.Value; anyDemo = true; }

        if (ages.Count > 0) demoObj["ages"] = ages;

        if (anyDemo)
        {
            try
            {
                metric.DemographicsJson = System.Text.Json.JsonSerializer.Serialize(demoObj);
            }
            catch
            {
                metric.DemographicsJson = null;
            }
        }

        return metric;
    }

    // Semaphore to limit per-process concurrent details requests.
    // Use lazy initialization to allow DI options to determine concurrency.
    private SemaphoreSlim? _detailsSemaphore;
    private SemaphoreSlim DetailsSemaphore => _detailsSemaphore ??= new SemaphoreSlim(Math.Max(1, _options.DetailsConcurrency));

    // Simple in-memory shop+endpoint back-off tracker to avoid hammering when 429 occurs.
    // Keyed by shop_cipher; stores the UTC time until which calls should be suspended.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _shopBackoffUntil = new();

    // Helper to compute delay for retries using exponential backoff with jitter.
    private static TimeSpan ComputeBackoffDelay(int attempt)
    {
        // attempt is 1-based: 1 -> 1s, 2 -> 2s, 3 -> 4s, 4 -> 8s, 5 -> 16s
        var baseSeconds = Math.Min(1 << (attempt - 1), 60);
        var jitter = new Random().NextDouble() * 0.25 + 0.75; // +/- 25% jitter
        var seconds = Math.Min(baseSeconds * jitter, 60);
        return TimeSpan.FromSeconds(seconds);
    }

    // Centralized HTTP GET with 429 handling for TikTok API requests.
    // path should be the exact request path (including video id when signing) and query contains signed query params (without sign yet).
    private async Task<(int StatusCode, string ResponseBody, System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>> Headers)> TikTokGetWithRetryAsync(HttpClient client, string pathWithQuery, Func<HttpRequestMessage> requestFactory, string shopCipher, CancellationToken cancellationToken)
    {
        // Check backoff suspension
        if (!string.IsNullOrWhiteSpace(shopCipher) && _shopBackoffUntil.TryGetValue(shopCipher, out var until) && DateTime.UtcNow < until)
        {
            // Immediately fail fast with 429 semantics for suspended shops
            return (429, string.Empty, System.Linq.Enumerable.Empty<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>>());
        }

        int maxRetries = 5;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            // honor cancellation
            cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var req = requestFactory();
                    using var res = await client.SendAsync(req, cancellationToken);
                    var body = await res.Content.ReadAsStringAsync(cancellationToken);

                    // If HTTP 429, handle via backoff/retry
                    if ((int)res.StatusCode == 429)
                    {
                        double retryAfterSeconds = -1;
                        if (res.Headers.TryGetValues("Retry-After", out var vals))
                        {
                            var raw = vals.FirstOrDefault();
                            if (double.TryParse(raw, out var parsed)) retryAfterSeconds = parsed;
                        }

                        TimeSpan delay = retryAfterSeconds > 0 ? TimeSpan.FromSeconds(Math.Min(retryAfterSeconds, 60)) : ComputeBackoffDelay(attempt);

                        var truncated = string.IsNullOrEmpty(body) ? "(empty)" : (body.Length > 4000 ? body.Substring(0, 4000) + "..." : body);
                        var requestId = "";
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            requestId = doc.RootElement.GetPropertyOrDefault("request_id") ?? string.Empty;
                        }
                        catch { }

                        _logger.LogWarning("TikTok HTTP 429 for shop {ShopCipher}. attempt={Attempt} retryAfter={RetryAfter}s backoff={BackoffMs}ms request_id={RequestId} body={Body}", shopCipher, attempt, retryAfterSeconds, (int)delay.TotalMilliseconds, requestId, truncated);

                        if (attempt == maxRetries)
                        {
                            var suspendUntil = DateTime.UtcNow.Add(delay);
                            if (!string.IsNullOrWhiteSpace(shopCipher)) _shopBackoffUntil.AddOrUpdate(shopCipher, suspendUntil, (_, __) => suspendUntil);
                            return ((int)res.StatusCode, body, res.Headers.SelectMany(h => h.Value, (h, v) => new KeyValuePair<string, IEnumerable<string>>(h.Key, new[] { v })));
                        }

                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }

                    // For non-429 status codes, attempt to detect TikTok business code rate limit (e.g., 36009002) in body
                    if ((int)res.StatusCode == 200 && !string.IsNullOrWhiteSpace(body))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            var root = doc.RootElement;
                            var code = root.TryGetProperty("code", out var codeElem) && codeElem.TryGetInt32(out var codeVal) ? codeVal : 0;
                            if (code == 36009002)
                            {
                                // treat as rate-limited - read Retry-After header if present
                                double retryAfterSeconds = -1;
                                if (res.Headers.TryGetValues("Retry-After", out var vals))
                                {
                                    var raw = vals.FirstOrDefault();
                                    if (double.TryParse(raw, out var parsed)) retryAfterSeconds = parsed;
                                }

                                TimeSpan delay = retryAfterSeconds > 0 ? TimeSpan.FromSeconds(Math.Min(retryAfterSeconds, 60)) : ComputeBackoffDelay(attempt);
                                var requestId = root.GetPropertyOrDefault("request_id");
                                var truncated = body.Length > 4000 ? body.Substring(0, 4000) + "..." : body;
                                _logger.LogWarning("TikTok business code rate limit for shop {ShopCipher}. code={Code} request_id={RequestId} attempt={Attempt} retryAfter={RetryAfter} backoffMs={BackoffMs} body={Body}", shopCipher, code, requestId, attempt, retryAfterSeconds, (int)delay.TotalMilliseconds, truncated);

                                if (attempt == maxRetries)
                                {
                                    var suspendUntil = DateTime.UtcNow.Add(delay);
                                    if (!string.IsNullOrWhiteSpace(shopCipher)) _shopBackoffUntil.AddOrUpdate(shopCipher, suspendUntil, (_, __) => suspendUntil);
                                    return ((int)res.StatusCode, body, res.Headers.SelectMany(h => h.Value, (h, v) => new KeyValuePair<string, IEnumerable<string>>(h.Key, new[] { v })));
                                }

                                await Task.Delay(delay, cancellationToken);
                                continue;
                            }
                        }
                        catch (JsonException)
                        {
                            // not JSON or unexpected shape - treat as normal response
                        }
                    }

                    // Normal successful or other non-429 response - return immediately
                    return ((int)res.StatusCode, body, res.Headers.SelectMany(h => h.Value, (h, v) => new KeyValuePair<string, IEnumerable<string>>(h.Key, new[] { v })));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // network error - last attempt will bubble up
                    _logger.LogDebug(ex, "TikTok HTTP transient exception on attempt {Attempt} for path {Path}", attempt, pathWithQuery);
                    if (attempt == maxRetries) throw;
                    var delay = ComputeBackoffDelay(attempt);
                    await Task.Delay(delay, cancellationToken);
                }
        }

        return (500, string.Empty, System.Linq.Enumerable.Empty<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>>());
    }

    // Development-only diagnostic: call the official v202509 per-video Details endpoint
    public async Task<(int? StatusCode, string Endpoint, JsonElement? Root, JsonElement? Data)> RunDetailsDiagnosticAsync(string videoId, string shopCipher, CancellationToken cancellationToken = default)
    {
        var client = _httpFactory.CreateClient("TikTokApi");

        var path = $"/analytics/202509/shop_videos/{videoId}/performance"; // exact endpoint provided by user

        // determine date range: prefer ContentLog.VideoPostTime if available, otherwise last 30 days
        DateTime startDate = DateTime.UtcNow.Date.AddDays(-30);
        DateTime endDate = DateTime.UtcNow.Date.AddDays(1);
        try
        {
            var existing = await _db.ContentLogs.AsNoTracking().FirstOrDefaultAsync(x => x.VideoId == videoId, cancellationToken);
            if (existing?.VideoPostTime != null)
            {
                var post = existing.VideoPostTime.Value.Date;
                startDate = post;
                endDate = post.AddDays(1);
            }
        }
        catch
        {
        }

        var credential = await _db.TikTokCredentials.FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (credential == null) return (null, path, null, null);

        var accessToken = await _authService.GetValidAccessTokenAsync(credential.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken)) return (null, path, null, null);

        var query = new Dictionary<string, string?>
        {
            { "app_key", _options.AppKey },
            { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
            { "shop_cipher", shopCipher },
            { "start_date_ge", startDate.ToString("yyyy-MM-dd") },
            { "end_date_lt", endDate.ToString("yyyy-MM-dd") }
        };

        var signInput = query.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var sign = _signatureService.GenerateSignature("GET", path, signInput, null, null);
        signInput.Add("sign", sign);

        var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(path, signInput);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // use decrypted raw access token per user instruction
        req.Headers.Add("x-tts-access-token", accessToken);
        req.Headers.Add("Accept", "application/json");

            // Acquire semaphore to bound concurrency and honor per-shop suspension inside helper
        await DetailsSemaphore.WaitAsync(cancellationToken);
        try
        {
            // pass a factory so the helper can create a fresh HttpRequestMessage per attempt
            var (status, body, headers) = await TikTokGetWithRetryAsync(client, url, () =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, url);
                r.Headers.Add("x-tts-access-token", accessToken);
                r.Headers.Add("Accept", "application/json");
                return r;
            }, shopCipher, cancellationToken);

            // Dev-only save of raw response
            try
            {
                if (_env != null && _env.IsDevelopment() && !string.IsNullOrWhiteSpace(body))
                {
                    var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "App_Data");
                    System.IO.Directory.CreateDirectory(dir);
                    var filePath = System.IO.Path.Combine(dir, $"tiktok-video-details-{videoId}.json");
                    await System.IO.File.WriteAllTextAsync(filePath, body, cancellationToken);
                }
            }
            catch
            {
            }

            if (status != 200)
            {
                return (status, url, null, null);
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement.Clone();
                var data = root.TryGetProperty("data", out var dataElem) ? dataElem.Clone() : root;
                return (status, url, root, data);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse TikTok details response for video {VideoId}", videoId);
                return (status, url, null, null);
            }
        }
        finally
        {
            DetailsSemaphore.Release();
        }
    }

    // Development-only diagnostic: perform a single Performance request and return status, endpoint, full JSON root and matched video element (if any).
    public async Task<(int? StatusCode, string Endpoint, JsonElement? Root, JsonElement? Matched)> RunPerformanceDiagnosticAsync(string videoId, string shopCipher, CancellationToken cancellationToken = default)
    {
        var client = _httpFactory.CreateClient("TikTokApi");
        var path = "/analytics/202605/shop_videos/performance";

        // determine date range: prefer ContentLog.VideoPostTime if available, otherwise last 30 days
        DateTime startDate = DateTime.UtcNow.Date.AddDays(-30);
        DateTime endDate = DateTime.UtcNow.Date.AddDays(1);
        try
        {
            var existing = await _db.ContentLogs.AsNoTracking().FirstOrDefaultAsync(x => x.VideoId == videoId, cancellationToken);
            if (existing?.VideoPostTime != null)
            {
                var post = existing.VideoPostTime.Value.Date;
                startDate = post;
                endDate = post.AddDays(1);
            }
        }
        catch
        {
            // fallback to defaults above
        }

        var pageToken = (string?)null;
        string? lastUrl = null;
        JsonElement? lastRoot = null;
        JsonElement? matched = null;

        var credential = await _db.TikTokCredentials.FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (credential == null) return (null, path, null, null);
        var accessToken = await _authService.GetValidAccessTokenAsync(credential.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken)) return (null, path, null, null);

        do
        {
            var query = new Dictionary<string, string?>
            {
                { "app_key", _options.AppKey },
                { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
                { "shop_cipher", shopCipher },
                { "start_date_ge", startDate.ToString("yyyy-MM-dd") },
                { "end_date_lt", endDate.ToString("yyyy-MM-dd") },
                { "page_size", "100" }
            };

            if (!string.IsNullOrWhiteSpace(pageToken)) query["page_token"] = pageToken;

            var signInput = query.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var sign = _signatureService.GenerateSignature("GET", path, signInput, null, null);
            signInput.Add("sign", sign);

            var url = QueryHelpers.AddQueryString(path, signInput);
            lastUrl = url;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-tts-access-token", accessToken);
            req.Headers.Add("Accept", "application/json");

            using var res = await client.SendAsync(req, cancellationToken);
            var content = await res.Content.ReadAsStringAsync(cancellationToken);

            // Dev-only save of page response
            try
            {
                if (_env != null && _env.IsDevelopment())
                {
                    var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "App_Data");
                    System.IO.Directory.CreateDirectory(dir);
                    var filePath = System.IO.Path.Combine(dir, "tiktok-video-performance-sample.json");
                    await System.IO.File.WriteAllTextAsync(filePath, content, cancellationToken);
                }
            }
            catch
            {
            }

            if (!res.IsSuccessStatusCode)
            {
                return ((int)res.StatusCode, lastUrl, null, null);
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement.Clone();
                lastRoot = root;

                // find matched video in root.data.videos array
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Object && dataElem.TryGetProperty("videos", out var videosElem) && videosElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in videosElem.EnumerateArray())
                    {
                        var vid = v.GetPropertyOrDefault("id") ?? v.GetPropertyOrDefault("video_id");
                        if (!string.IsNullOrWhiteSpace(vid) && vid == videoId)
                        {
                            matched = v.Clone();
                            break;
                        }
                    }

                    // determine next page token
                    var nextPage = dataElem.GetPropertyOrDefault("page_token") ?? dataElem.GetPropertyOrDefault("next_page_token");
                    if (nextPage == null && dataElem.TryGetProperty("page_info", out var pageInfo) && pageInfo.ValueKind == JsonValueKind.Object)
                    {
                        nextPage = pageInfo.GetPropertyOrDefault("page_token") ?? pageInfo.GetPropertyOrDefault("next_page_token");
                    }

                    pageToken = string.IsNullOrWhiteSpace(nextPage) ? null : nextPage;
                }
                else
                {
                    pageToken = null;
                }

                if (matched.HasValue)
                {
                    return ((int)res.StatusCode, lastUrl, lastRoot, matched);
                }
            }
            catch
            {
                return ((int)res.StatusCode, lastUrl, null, null);
            }

            // continue to next page if available
        } while (!string.IsNullOrWhiteSpace(pageToken));

        return (200, lastUrl ?? path, lastRoot, matched);
    }

    // Fetch details for a single video and return JsonElement if successful, or null on failure.
    private async Task<JsonElement?> FetchVideoDetailsSingleAsync(HttpClient client, string videoId, string shopCipher, CancellationToken cancellationToken)
    {
        // Construct path using configured DetailsPath (may contain placeholders {video_id})
        var path = _options.DetailsPath ?? "/analytics/202509/shop_videos/{video_id}/performance";

        var query = new Dictionary<string, string?>
        {
            { "app_key", _options.AppKey },
            { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
            { "shop_cipher", shopCipher }
        };

        // If path contains {video_id} placeholder, replace it; otherwise put video_id in query
        if (path.Contains("{video_id}"))
        {
            path = path.Replace("{video_id}", videoId);
        }
        else
        {
            query["video_id"] = videoId;
        }

        var signInput = query.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var sign = _signatureService.GenerateSignature("GET", path, signInput, null, null);
        signInput.Add("sign", sign);

        var url = QueryHelpers.AddQueryString(path, signInput);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var credential = await _db.TikTokCredentials.FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (credential == null) return null;
        var accessToken = await _authService.GetValidAccessTokenAsync(credential.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        req.Headers.Add("x-tts-access-token", accessToken);
        req.Headers.Add("Accept", "application/json");

        using var res = await client.SendAsync(req, cancellationToken);
        var content = await res.Content.ReadAsStringAsync(cancellationToken);

        // Development-only: save raw response if env is development
        try
        {
            if (_env != null && _env.IsDevelopment())
            {
                var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "App_Data");
                System.IO.Directory.CreateDirectory(dir);
                var filePath = System.IO.Path.Combine(dir, "tiktok-video-details-sample.json");
                await System.IO.File.WriteAllTextAsync(filePath, content, cancellationToken);
                _logger.LogInformation("Saved details sample to {File}", filePath);
            }
        }
        catch
        {
            // ignore file write errors in diagnostics
        }

        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("Details request failed {Status} for video {VideoId}: {Content}", (int)res.StatusCode, videoId, content);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed parsing JSON details for video {VideoId}", videoId);
            return null;
        }
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

                    // Details enrichment is intentionally skipped in the dry-run path to keep diagnostic behavior read-only.

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
        var processedVideoIds = new HashSet<string>();
        var metricsToSave = new List<ContentMetric>();
        // Development-only diagnostics counters
        var totalVideosReceived = 0;
        var totalVideosMatched = 0;
        var totalVideosWithViews = 0;
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
                    totalVideosReceived++;
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
                    ContentLog? createdCl = null;
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
                        createdCl = cl;
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

                    // determine target ContentLog (either existing or newly created)
                    var targetLog = existing ?? createdCl;

                    // parse views if present
                    long? views = null;
                    if (v.TryGetProperty("views", out var viewsElem) && viewsElem.ValueKind == JsonValueKind.Number && viewsElem.TryGetInt64(out var vval))
                    {
                        views = vval;
                        totalVideosWithViews++;
                    }

                    if (targetLog != null)
                    {
                        totalVideosMatched++;
                        // create metric only once per video id per sync run; keep a reference for potential enrichment
                        ContentMetric? viewsMetric = null;
                        if (views.HasValue && processedVideoIds.Add(videoId))
                        {
                            var metric = new ContentMetric
                            {
                                ContentLog = targetLog,
                                Views = views,
                                MetricStartDate = effectiveStartDate,
                                MetricEndDate = effectiveEndDate,
                                CapturedAt = DateTime.UtcNow
                            };
                            metricsToSave.Add(metric);
                            viewsMetric = metric;
                        }
                        // Details enrichment is now handled by a separate DetailsSync service.
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

                if (metricsToSave.Count > 0)
                {
                    if (_env.IsDevelopment())
                    {
                        _logger.LogInformation("ContentMetrics diagnostic: Videos received = {Received}, Videos matched = {Matched}, Videos with views = {WithViews}, Metrics queued = {Queued}",
                            totalVideosReceived,
                            totalVideosMatched,
                            totalVideosWithViews,
                            metricsToSave.Count);
                    }

                    // Persist metrics (will be saved together with ContentLogs/updates in the same SaveChanges call)
                    // no-op patch: ensure persistence section remains unchanged
                    _db.ContentMetrics.AddRange(metricsToSave);
                }

                if (toSave.Count > 0 || toUpdate.Count > 0 || metricsToSave.Count > 0)
                {
                    _logger.LogInformation(
                        "Saving TikTok video page for shop {ShopCipher}. " +
                        "ToSave={ToSaveCount}, ToUpdate={ToUpdateCount}, Metrics={MetricsCount}",
                        shopCipher,
                        toSave.Count,
                        toUpdate.Count,
                        metricsToSave.Count);

                    try
                    {
                        await _db.SaveChangesAsync(cancellationToken);
                        // clear metrics list so we don't re-add same instances
                        var savedCount = metricsToSave.Count;
                        metricsToSave.Clear();

                        // Development-only diagnostic logging
                        if (_env != null && _env.IsDevelopment())
                        {
                            _logger.LogInformation("ContentMetrics diagnostic: Videos received = {Received}, Videos matched = {Matched}, Videos with views = {WithViews}, Metrics queued = {Queued}, Metrics saved = {Saved}",
                                totalVideosReceived,
                                totalVideosMatched,
                                totalVideosWithViews,
                                savedCount,
                                savedCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Failed saving TikTok videos for shop {ShopCipher}. " +
                            "ToSave={ToSaveCount}, ToUpdate={ToUpdateCount}, Metrics={MetricsCount}",
                            shopCipher,
                            toSave.Count,
                            toUpdate.Count,
                            metricsToSave.Count);

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
