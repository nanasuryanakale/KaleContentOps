using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Services.TikTok;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KaleContentOps.Services.CreativePerformance;

/// <summary>
/// Production account-level daily metric source backed by the VERIFIED TikTok Shop
/// Analytics endpoint:
///
///   GET /analytics/202609/shop_videos/overview_performance
///   (scope: data.shop_analytics.public.read)
///
/// Verification (2026-10-21, official API Testing Tool, code=0 "Success"):
/// with start_date_ge / end_date_lt / granularity=1D / currency=LOCAL /
/// account_type=ALL / today=false the response carries data.latest_available_date
/// and daily intervals (start_date, end_date exclusive) with a numeric
/// video_views field. Six verified days (2026-10-14..19) sum to 185.855,
/// matching the aggregate response - so daily mapping is exact.
///
/// MAPPED (verified against the live response at field level):
///   - video_views -> <see cref="AccountDailyMetricRecord.VideoViews"/>
///   - product_impressions, product_clicks
///   - video_attributed_sku_orders, video_gmv.amount, video_indirect_gmv.amount,
///     shoppable_video_attributed_gmv.amount
///   - ctr, ctor_sku_order, gpm.amount (+ currency), avg_daily_customers,
///     video_product_viewers
///
/// NOT mapped by design (different meaning, never forced into a different family):
/// Reach / Interactions / Engagement Rate / Net Follower / Total Follower come from
/// other metric families. Null != 0: a field that is absent on a given day stays null
/// so a partial day never turns into a fabricated zero. Percentages and averages are
/// rejected: ctr/ctor_sku_order/gpm are read and stored as READ-ONLY metadata on the
/// daily record; they are never summed or averaged across days. GMV subtypes
/// (video_gmv.amount, video_indirect_gmv.amount, shoppable_video_attributed_gmv.amount)
/// are kept as separate READ-ONLY fields and are NEVER summed into a single GMV figure.
/// </summary>
public sealed class TikTokShopVideoOverviewMetricSource : IAccountDailyMetricSource
{
    public const string EndpointPath = "/analytics/202609/shop_videos/overview_performance";

    public const string ProviderSourceName =
        "TikTok Shop Video Performance Overview API (analytics/202609)";

    /// <summary>
    /// Total transport attempts (1 initial + retries) before the request is honestly
    /// reported as unavailable. PROVEN necessary (2026-10-21, live local run): the
    /// shop network intermittently times out at TCP-connect level against
    /// open-api.tiktokglobalshop.com - the identical signed request succeeded seconds
    /// before and after one such failure, and a plain curl to the same host alternates
    /// between connect-timeout and success. Only TRANSPORT failures are retried;
    /// HTTP-level and TikTok business errors are never retried here (rate-limit and
    /// permanent errors are handled by the response classification below).
    /// </summary>
    public const int MaxTransportAttempts = 3;

    /// <summary>Linear retry backoff base in seconds (1s, then 2s).</summary>
    public const int TransportRetryBaseDelaySeconds = 1;

    /// <summary>
    /// Metrics verified to exist on this endpoint at the RECORD level (per daily
    /// interval) in the live response shape used by this source. Declared so the
    /// dashboard renders every metric outside this set as Unavailable ("-") instead
    /// of "no data recorded" or a leaked value from an unrelated field.
    /// </summary>
    private static readonly IReadOnlySet<string> VerifiedMetricKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        DailyReportMetrics.VideoViews,
        // Dashboard summary metrics served by this endpoint (shop-video commerce
        // funnel). They are their OWN metrics - never Reach / Interactions /
        // Engagement Rate / follower substitutes.
        DailyReportMetrics.ProductImpressions,
        DailyReportMetrics.ProductClicks,
        DailyReportMetrics.VideoAttributedSkuOrders,
        TikTokShopVideoOverviewMetricSourceMetrics.VideoViews,
        TikTokShopVideoOverviewMetricSourceMetrics.ProductImpressions,
        TikTokShopVideoOverviewMetricSourceMetrics.ProductClicks,
        TikTokShopVideoOverviewMetricSourceMetrics.VideoAttributedSkuOrders,
        TikTokShopVideoOverviewMetricSourceMetrics.VideoAttributedGmv,
        TikTokShopVideoOverviewMetricSourceMetrics.ShoppableVideoAttributedGmv,
        TikTokShopVideoOverviewMetricSourceMetrics.VideoGmv,
        TikTokShopVideoOverviewMetricSourceMetrics.VideoIndirectGmv,
        TikTokShopVideoOverviewMetricSourceMetrics.Ctr,
        TikTokShopVideoOverviewMetricSourceMetrics.CtorSkuOrder,
        TikTokShopVideoOverviewMetricSourceMetrics.Gpm,
        TikTokShopVideoOverviewMetricSourceMetrics.AvgDailyCustomers,
        TikTokShopVideoOverviewMetricSourceMetrics.VideoProductViewers,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly TikTokOptions _options;
    private readonly AppDbContext _db;
    private readonly ITikTokSignatureService _signatureService;
    private readonly ITikTokAuthService _authService;
    private readonly ILogger<TikTokShopVideoOverviewMetricSource> _logger;

    public TikTokShopVideoOverviewMetricSource(
        IHttpClientFactory httpFactory,
        IOptions<TikTokOptions> options,
        AppDbContext db,
        ITikTokSignatureService signatureService,
        ITikTokAuthService authService,
        ILogger<TikTokShopVideoOverviewMetricSource>? logger = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _db = db;
        _signatureService = signatureService;
        _authService = authService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TikTokShopVideoOverviewMetricSource>.Instance;
    }

    public async Task<AccountDailyMetricResult> GetAsync(AccountDailyMetricQuery query, CancellationToken cancellationToken = default)
    {
        // Safe diagnostics (no tokens, no secrets, no signatures): establishes whether
        // the provider is invoked at all and with which half-open window.
        _logger.LogInformation(
            "Daily Report overview source invoked for {Start}..{EndExclusive} (AppKey configured: {HasKey})",
            query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            !string.IsNullOrWhiteSpace(_options.AppKey) && !string.IsNullOrWhiteSpace(_options.AppSecret));

        if (string.IsNullOrWhiteSpace(_options.AppKey) || string.IsNullOrWhiteSpace(_options.AppSecret))
        {
            _logger.LogWarning(
                "Daily Report overview source unavailable: TikTok:AppKey/AppSecret not configured ({Start}..{EndExclusive})",
                query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return AccountDailyMetricResult.Unavailable(
                "Kredensial TikTok (TikTok:AppKey/AppSecret) belum dikonfigurasi - integrasi analytics level akun belum aktif.");
        }

        // Shop cipher: explicit option first, otherwise the first registered shop
        // (same TikTokShops table the sync services resolve from).
        var shopCipher = string.IsNullOrWhiteSpace(_options.DefaultShopCipher)
            ? await _db.TikTokShops.AsNoTracking()
                .Where(s => s.ShopCipher != null && s.ShopCipher != "")
                .OrderBy(s => s.Id)
                .Select(s => s.ShopCipher)
                .FirstOrDefaultAsync(cancellationToken)
            : _options.DefaultShopCipher;

        if (string.IsNullOrWhiteSpace(shopCipher))
        {
            _logger.LogWarning(
                "Daily Report overview source unavailable: no registered TikTok shop with a non-empty ShopCipher ({Start}..{EndExclusive})",
                query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return AccountDailyMetricResult.Unavailable(
                "Belum ada shop TikTok yang terdaftar (cipher kosong) - data analytics level akun tidak dapat diambil.");
        }

        var credential = await _db.TikTokCredentials.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (credential is null)
        {
            _logger.LogWarning(
                "Daily Report overview source unavailable: no TikTok credential row matches the configured AppKey (shop {ShopCipher})",
                shopCipher);
            return AccountDailyMetricResult.Unavailable(
                "Kredensial TikTok belum tersedia (token belum di-exchange) - integrasi analytics level akun belum aktif.");
        }

        string? accessToken;
        try
        {
            accessToken = await _authService.GetValidAccessTokenAsync(credential.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TikTok access token refresh failed for the Daily Report overview source");
            return AccountDailyMetricResult.Unavailable(
                "Access token TikTok tidak tersedia (refresh gagal) - data analytics level akun belum dapat diambil.");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogWarning(
                "Daily Report overview source unavailable: access token empty for credential {CredentialId} (shop {ShopCipher})",
                credential.Id, shopCipher);
            return AccountDailyMetricResult.Unavailable(
                "Access token TikTok tidak tersedia - data analytics level akun belum dapat diambil.");
        }

        _logger.LogInformation(
            "Daily Report overview source: shop {ShopCipher}, requesting {Route} ({Start}..{EndExclusive})",
            shopCipher, EndpointPath,
            query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var client = _httpFactory.CreateClient("TikTokApi");
        string content;
        System.Net.HttpStatusCode httpStatus;

        // Verified request parameters (API Testing Tool, 2026-10-21). The half-open
        // window is forwarded verbatim; granularity=1D is what makes the daily
        // intervals (and therefore the daily video_views mapping) valid.
        //
        // Bounded TRANSPORT retry (see MaxTransportAttempts): the timestamp and the
        // signature are regenerated per attempt, so every attempt is a fully valid
        // fresh request. HTTP-level and TikTok business errors are NOT retried here.
        for (var attempt = 1; ; attempt++)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var queryDict = new Dictionary<string, string?>
            {
                { "app_key", _options.AppKey },
                { "timestamp", timestamp },
                { "shop_cipher", shopCipher },
                { "start_date_ge", query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "end_date_lt", query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "granularity", "1D" },
                { "currency", "LOCAL" },
                { "account_type", "ALL" },
                { "today", "false" }
            };

            var signInput = queryDict.Where(kv => !string.IsNullOrEmpty(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var sign = _signatureService.GenerateSignature("GET", EndpointPath, signInput, null, null);
            signInput.Add("sign", sign);

            var url = QueryHelpers.AddQueryString(EndpointPath, signInput);

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("x-tts-access-token", accessToken);
                req.Headers.Add("Accept", "application/json");

                using var res = await client.SendAsync(req, cancellationToken);
                httpStatus = res.StatusCode;
                content = await res.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxTransportAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "TikTok shop-videos overview request failed (HTTP transport), attempt {Attempt}/{MaxAttempts} of {Route} for shop {ShopCipher} - retrying",
                    attempt, MaxTransportAttempts, EndpointPath, shopCipher);
                await Task.Delay(TimeSpan.FromSeconds(TransportRetryBaseDelaySeconds * attempt), cancellationToken);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "TikTok shop-videos overview request failed (HTTP transport) after {MaxAttempts} attempt(s) of {Route} for shop {ShopCipher}",
                    MaxTransportAttempts, EndpointPath, shopCipher);
                return AccountDailyMetricResult.Unavailable(
                    "Request TikTok Shop Analytics gagal (jaringan/transport) - data video views level akun belum dapat diambil.");
            }

            break;
        }

        try
        {
            // HTTP-level failure first: an error status must never be described as a
            // payload-format problem (and the body is not parsed for data).
            if ((int)httpStatus < 200 || (int)httpStatus >= 300)
            {
                string? errCode = null, errMessage = null, errRequestId = null;
                try
                {
                    using var errDoc = JsonDocument.Parse(content);
                    if (errDoc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        errCode = errDoc.RootElement.GetPropertyOrDefault("code");
                        errMessage = errDoc.RootElement.GetPropertyOrDefault("message");
                        errRequestId = errDoc.RootElement.GetPropertyOrDefault("request_id");
                    }
                }
                catch (JsonException)
                {
                    // Error body is not JSON - report only the HTTP status.
                }

                _logger.LogWarning(
                    "TikTok shop-videos overview HTTP {Status}: code {Code}, message {Message}, request_id {RequestId}",
                    (int)httpStatus, errCode, errMessage, errRequestId);
                return AccountDailyMetricResult.Unavailable(
                    $"TikTok Shop Analytics merespons HTTP {(int)httpStatus}" +
                    (errCode is null ? "." : $" (code {errCode}: {errMessage}, request_id={errRequestId ?? "-"})."));
            }

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("TikTok shop-videos overview: unexpected root JSON kind {Kind}, HTTP {Status}", root.ValueKind, (int)httpStatus);
                return AccountDailyMetricResult.Unavailable(
                    "Respons TikTok Shop Analytics tidak sesuai format - data video views level akun belum dapat dibaca.");
            }

            var code = ReadInt32Flexible(root, "code") ?? 0;
            var message = root.GetPropertyOrDefault("message");
            var requestId = root.GetPropertyOrDefault("request_id");
            if (code != 0)
            {
                _logger.LogWarning(
                    "TikTok shop-videos overview business error: code {Code}, message {Message}, request_id {RequestId}, HTTP {Status}",
                    code, message, requestId, (int)httpStatus);
                return AccountDailyMetricResult.Unavailable(
                    $"TikTok Shop Analytics menolak permintaan (code {code}: {message ?? "no message"}, request_id={requestId ?? "-"}).");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return AccountDailyMetricResult.Unavailable(
                    "Respons TikTok Shop Analytics tidak berisi objek data - data video views level akun belum dapat dibaca.");
            }

            // Daily intervals: data.performance.intervals (verified shape family of
            // the 202609 analytics endpoints); tolerate data.intervals as fallback.
            if (!TryGetIntervals(data, out var intervals))
            {
                return AccountDailyMetricResult.Unavailable(
                    "Respons TikTok Shop Analytics tidak berisi interval harian (granularity=1D) - data video views level akun belum dapat dibaca.");
            }

            // latest_available_date: intervals dated after it are not yet complete
            // and must not be shown (they would silently change later).
            DateTime? latestAvailable = ParseDate(GetChild(data, "latest_available_date") ?? GetChild(root, "latest_available_date"));

            var records = new List<AccountDailyMetricRecord>();
            var skippedMultiDay = 0;
            var skippedAfterLatest = 0;
            foreach (var interval in intervals.EnumerateArray())
            {
                if (interval.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var start = ParseDate(interval.GetPropertyOrDefault("start_date"));
                if (start is null)
                {
                    continue;
                }

                // end_date is EXCLUSIVE: a daily (1D) interval must be exactly one
                // day. A multi-day interval cannot be attributed to a single day,
                // so it is skipped rather than fabricated into daily values.
                var end = ParseDate(interval.GetPropertyOrDefault("end_date"));
                if (end is not null && end.Value != start.Value.AddDays(1))
                {
                    skippedMultiDay++;
                    continue;
                }

                if (latestAvailable is not null && start.Value > latestAvailable.Value)
                {
                    skippedAfterLatest++;
                    continue;
                }

                var startUtc = query.Start; // normalized calendar date (midnight)
                if (start.Value < startUtc || start.Value >= query.EndExclusive)
                {
                    continue; // outside the half-open requested window
                }

                // Verified DAILY record-level fields (live response shape, 2026-10-21):
                // - video_views -> VideoViews (the only dashboard metric mapped here)
                // - product_impressions, product_clicks -> READ-ONLY record metadata
//   (NOT Reach/Interactions/followers)
                // - video_attributed_sku_orders -> READ-ONLY record metadata
                // - video_attributed_gmv.amount + currency -> READ-ONLY GMV subtype metadata
//   (NEVER summed with other GMV subtypes)
                // - shoppable_video_attributed_gmv.amount + currency -> READ-ONLY GMV subtype metadata
                // - video_gmv.amount + currency -> READ-ONLY GMV subtype metadata
                // - video_indirect_gmv.amount + currency -> READ-ONLY GMV subtype metadata
                // - ctr, ctor_sku_order -> READ-ONLY percentage/fraction metadata
//   (never summed or averaged)
                // - gpm.amount + currency -> READ-ONLY metadata
                // - avg_daily_customers -> READ-ONLY metadata
                // - video_product_viewers -> READ-ONLY record metadata
//   (NEVER used as Reach)

                var videoViews = ReadInt64Flexible(interval, "video_views");
                var productImpressions = ReadInt64Flexible(interval, "product_impressions");
                var productClicks = ReadInt64Flexible(interval, "product_clicks");
                var videoAttributedSkuOrders = ReadInt64Flexible(interval, "video_attributed_sku_orders");

                var videoAttributedGmv = ReadAmountField(interval, "video_attributed_gmv", "amount");
                var shoppableVideoAttributedGmv = ReadAmountField(interval, "shoppable_video_attributed_gmv", "amount");
                var videoGmv = ReadAmountField(interval, "video_gmv", "amount");
                var videoIndirectGmv = ReadAmountField(interval, "video_indirect_gmv", "amount");

                var ctr = ReadDecimalFraction(interval, "ctr");
                var ctorSkuOrder = ReadDecimalFraction(interval, "ctor_sku_order");
                var gpm = ReadAmountField(interval, "gpm", "amount");

                var avgDailyCustomers = ReadDecimalFlexible(interval, "avg_daily_customers");
                var videoProductViewers = ReadInt64Flexible(interval, "video_product_viewers");

                records.Add(new AccountDailyMetricRecord
                {
                    Date = start.Value,
                    VideoViews = videoViews,
                    ProductImpressions = productImpressions,
                    ProductClicks = productClicks,
                    VideoAttributedSkuOrders = videoAttributedSkuOrders,
                    VideoAttributedGmvAmount = videoAttributedGmv.Amount,
                    VideoAttributedGmvCurrency = videoAttributedGmv.Currency,
                    ShoppableVideoAttributedGmvAmount = shoppableVideoAttributedGmv.Amount,
                    ShoppableVideoAttributedGmvCurrency = shoppableVideoAttributedGmv.Currency,
                    VideoGmvAmount = videoGmv.Amount,
                    VideoGmvCurrency = videoGmv.Currency,
                    VideoIndirectGmvAmount = videoIndirectGmv.Amount,
                    VideoIndirectGmvCurrency = videoIndirectGmv.Currency,
                    Ctr = ctr,
                    CtorSkuOrder = ctorSkuOrder,
                    GpmAmount = gpm.Amount,
                    GpmCurrency = gpm.Currency,
                    AvgDailyCustomers = avgDailyCustomers,
                    VideoProductViewers = videoProductViewers
                    // Reach, ProfileViews, Likes, Comments, Shares, FollowersGained,
                    // FollowersLost: intentionally null - unverified for this endpoint.
                });
            }

            // Safe response summary (no payload body, no tokens): proves the provider
            // answered, how many intervals TikTok returned and how many daily records
            // were kept inside the requested half-open window.
            _logger.LogInformation(
                "TikTok shop-videos overview response: HTTP {Status}, code {Code}, request_id {RequestId}, latest_available_date {Latest}, intervals returned {Intervals}, records kept in window {Records}",
                (int)httpStatus, code, requestId,
                latestAvailable?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-",
                intervals.GetArrayLength(), records.Count);

            if (skippedMultiDay > 0 || skippedAfterLatest > 0)
            {
                _logger.LogInformation(
                    "TikTok shop-videos overview: skipped {MultiDay} multi-day interval(s) and {AfterLatest} interval(s) after latest_available_date={Latest}",
                    skippedMultiDay, skippedAfterLatest, latestAvailable?.ToString("yyyy-MM-dd") ?? "-");
            }

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records
                    .OrderBy(r => r.Date)
                    .ToList(),
                FollowerSnapshots = Array.Empty<FollowerSnapshot>(),
                ViewerComposition = null, // unverified for this endpoint
                SourceName = ProviderSourceName,
                AvailableMetricKeys = VerifiedMetricKeys
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "TikTok shop-videos overview: malformed JSON payload");
            return AccountDailyMetricResult.Unavailable(
                "Respons TikTok Shop Analytics bukan JSON yang valid - data video views level akun belum dapat dibaca.");
        }
    }

    // ------------------------------------------------------------------
    // Tolerant JSON helpers (no invented fields - only accepted variants)
    // ------------------------------------------------------------------

    private static bool TryGetIntervals(JsonElement data, out JsonElement intervals)
    {
        // Verified family shape: data.performance.intervals. Fallback: data.intervals.
        if (TryGetArray(data, "performance", out var performance)
            && TryGetArrayChild(performance, "intervals", out intervals))
        {
            return true;
        }

        if (TryGetArrayChild(data, "intervals", out intervals))
        {
            return true;
        }

        intervals = default;
        return false;
    }

    private static bool TryGetArray(JsonElement parent, string name, out JsonElement child)
    {
        child = default;
        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(name, out child)
               && child.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArrayChild(JsonElement parent, string name, out JsonElement child)
    {
        child = default;
        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(name, out child)
               && child.ValueKind == JsonValueKind.Array;
    }

    private static string? GetChild(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object ? parent.GetPropertyOrDefault(name) : null;

    /// <summary>Accepts numeric and string-encoded integers (TikTok uses both).</summary>
    private static long? ReadInt64Flexible(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var elem))
        {
            return null;
        }

        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt64(out var numeric))
        {
            return numeric;
        }

        if (elem.ValueKind == JsonValueKind.String
            && long.TryParse(elem.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? ReadInt32Flexible(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var elem))
        {
            return null;
        }

        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (elem.ValueKind == JsonValueKind.String
            && int.TryParse(elem.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Reads a decimal fraction such as ctr or ctor_sku_order. TikTok may return a
    /// string like "0.0545" or a numeric 0.0545. Null when absent. Stored READ-ONLY
    /// as a fraction (NOT a percentage) so nothing downstream accidentally treats it
    /// as a percentage and sums/averages it with other days.
    /// </summary>
    private static decimal? ReadDecimalFraction(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var elem))
        {
            return null;
        }

        return elem.ValueKind switch
        {
            JsonValueKind.Number when elem.TryGetDecimal(out var numeric) => numeric,
            JsonValueKind.String when decimal.TryParse(elem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// Reads a decimal value such as avg_daily_customers. Accepts numeric and
    /// string-encoded forms. Null when absent. Stored READ-ONLY.
    /// </summary>
    private static decimal? ReadDecimalFlexible(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var elem))
        {
            return null;
        }

        return elem.ValueKind switch
        {
            JsonValueKind.Number when elem.TryGetDecimal(out var numeric) => numeric,
            JsonValueKind.String when decimal.TryParse(elem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// Reads a nested amount object such as "video_attributed_gmv": { "amount": "1500.00", "currency": "IDR" }.
    /// Returns (Amount, Currency). Both null when the object or the amount field is absent.
    /// Currency is kept because GMV amounts carry their currency and must not be combined
    /// with another currency blindly.
    /// </summary>
    private static (decimal? Amount, string? Currency) ReadAmountField(JsonElement parent, string objName, string amountFieldName)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(objName, out var obj)
            || obj.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        decimal? amount = null;
        string? currency = null;

        if (obj.TryGetProperty(amountFieldName, out var amountElem))
        {
            amount = amountElem.ValueKind switch
            {
                JsonValueKind.Number when amountElem.TryGetDecimal(out var numeric) => numeric,
                JsonValueKind.String when decimal.TryParse(amountElem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
        }

        if (obj.TryGetProperty("currency", out var currencyElem)
            && currencyElem.ValueKind == JsonValueKind.String)
        {
            currency = IsKnownCurrencyCode(currencyElem.GetString(), out var validCode) ? validCode : null;
        }

        return (amount, currency);
    }

    /// <summary>Parses an ISO yyyy-MM-dd date-only value; null when absent/invalid.</summary>
    private static DateTime? ParseDate(string? raw)
        => !string.IsNullOrWhiteSpace(raw)
           && DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date
            : null;

    /// <summary>
    /// Treats a currency string as present only when it looks like a real currency
    /// code (exactly three alphabetic characters - ISO 4217, e.g. "IDR", "USD").
    /// "0", "0.00", empty, and other non-code strings are treated as absent so a
    /// malformed currency value is never silently combined with a GMV amount.
    /// When the value is valid, <paramref name="validCode"/> is set to the currency
    /// code so callers get the normalised value in one step.
    /// </summary>
    private static bool IsKnownCurrencyCode(string? raw, out string? validCode)
    {
        validCode = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 3)
        {
            return false;
        }

        foreach (var c in raw)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        validCode = raw;
        return true;
    }
}
