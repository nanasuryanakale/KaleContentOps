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

    /// <summary>
    /// Paginated video LIST endpoint used for the PERIOD interaction totals (verified
    /// live 2026-10-22, code=0): each data.videos[] entry carries windowed top-level
    /// likes / comments / shares for the requested start_date_ge/end_date_lt window
    /// (values change with the window - proven by probing 1-day vs 7-day windows).
    /// The endpoint has NO daily intervals for these fields, so they are summed to a
    /// period total and never distributed across days. Same scope as the overview
    /// endpoint (data.shop_analytics.public.read).
    /// </summary>
    public const string ListEndpointPath = "/analytics/202605/shop_videos/performance";

    /// <summary>
    /// Batch endpoint used for the DAILY interaction breakdown (verified live 2026-10-22,
    /// code=0): GET /analytics/202609/shop_videos/batch_performance with video_ids
    /// (comma-separated) and granularity=1D returns per-video DAILY intervals whose
    /// traffic object carries likes / comments / shares / views / new_followers.
    /// ONE request covers up to <see cref="BatchChunkSize"/> videos, so a whole-window
    /// daily breakdown costs ~6 requests instead of the ~28 the paginated list needs.
    /// </summary>
    public const string BatchEndpointPath = "/analytics/202609/shop_videos/batch_performance";

    /// <summary>Maximum video_ids per batch request (documented API limit).</summary>
    public const int BatchChunkSize = 500;

    /// <summary>
    /// VERIFIED window span limit for granularity=1D on the batch endpoint (probes
    /// 2026-10-22): a 7-day span is accepted (even at an old position - 2026-10-01..08
    /// returned code=0), an 8-day span is rejected with HTTP 400 "Invalid Parameter".
    /// Longer report windows are therefore split into sub-windows of at most 7 days.
    /// </summary>
    public const int BatchMaxWindowDays = 7;

    /// <summary>
    /// Hard bound on one window's batch fan-out: video chunks x day sub-windows.
    /// A 14-day window over ~2.800 videos costs 2 x 6 = 12 requests (vs ~28 list pages);
    /// anything beyond this budget degrades to the list-based period totals.
    /// </summary>
    public const int BatchMaxFanOutRequests = 16;

    /// <summary>
    /// Hard guard on how many local catalog videos are sent per window (12 chunks of 500).
    /// The shop catalog is ~2.800 videos; exceeding the guard degrades to the list-based
    /// period totals instead of an unbounded fan-out.
    /// </summary>
    public const int BatchMaxVideos = 6000;

    /// <summary>TikTok business code observed for a window outside the 202609 lookback.</summary>
    public const int BatchLookbackErrorCode = 28001022;

    /// <summary>Page size VERIFIED working on the list endpoint (same value the list sync uses).</summary>
    public const int ListPageSize = 100;

    /// <summary>
    /// Hard pagination guard for one window: 100 pages x 100 videos = 10.000 unique videos
    /// (the shop currently returns total_count=2589, i.e. ~26 pages). When the guard trips,
    /// the interaction totals are reported UNAVAILABLE instead of returning a partial sum
    /// dressed up as a complete period value.
    /// </summary>
    public const int ListMaxPages = 100;

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
    private readonly TikTokPeriodInteractionsCache? _interactionsCache;
    private readonly ITikTokVideoService? _videoService;
    private readonly ILogger<TikTokShopVideoOverviewMetricSource> _logger;
    private readonly Microsoft.Extensions.Hosting.IHostEnvironment? _environment;

    public TikTokShopVideoOverviewMetricSource(
        IHttpClientFactory httpFactory,
        IOptions<TikTokOptions> options,
        AppDbContext db,
        ITikTokSignatureService signatureService,
        ITikTokAuthService authService,
        ILogger<TikTokShopVideoOverviewMetricSource>? logger = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        TikTokPeriodInteractionsCache? interactionsCache = null,
        ITikTokVideoService? videoService = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _db = db;
        _signatureService = signatureService;
        _authService = authService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TikTokShopVideoOverviewMetricSource>.Instance;
        _environment = environment;
        _interactionsCache = interactionsCache;
        _videoService = videoService;
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
        var shopCipherFromConfig = !string.IsNullOrWhiteSpace(_options.DefaultShopCipher);
        var shopCipher = shopCipherFromConfig
            ? _options.DefaultShopCipher
            : await _db.TikTokShops.AsNoTracking()
                .Where(s => s.ShopCipher != null && s.ShopCipher != "")
                .OrderBy(s => s.Id)
                .Select(s => s.ShopCipher)
                .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(shopCipher))
        {
            // Safe diagnostics (no tokens/secrets): row count proves whether the
            // Development database simply has no registered shop yet - a different
            // root cause than a shop row with an empty cipher.
            var shopRowCount = await _db.TikTokShops.AsNoTracking().CountAsync(cancellationToken);
            _logger.LogWarning(
                "Daily Report overview source unavailable: no registered TikTok shop with a non-empty ShopCipher ({Start}..{EndExclusive}): TikTokShops rows in database = {ShopRows}, TikTok:DefaultShopCipher configured = {HasCipherOption}. Set TikTok:DefaultShopCipher (user secrets / environment) or complete the TikTok OAuth flow and use Sync Authorized Shops on the Admin TikTok page.",
                query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                shopRowCount,
                shopCipherFromConfig);
            return AccountDailyMetricResult.Unavailable(
                "Belum ada shop TikTok yang terdaftar (cipher kosong) - data analytics level akun tidak dapat diambil. " +
                "Atur TikTok:DefaultShopCipher pada konfigurasi Development, atau selesaikan otorisasi TikTok lalu jalankan Sync Authorized Shops di halaman Admin TikTok.");
        }

        var credential = await _db.TikTokCredentials.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (credential is null)
        {
            _logger.LogWarning(
                "Daily Report overview source unavailable: no TikTok credential row matches the configured AppKey (shop {ShopCipher}, cipher source {CipherSource}) - complete the TikTok OAuth callback once on this environment to create the credential row",
                shopCipher,
                shopCipherFromConfig ? "TikTok:DefaultShopCipher config" : "TikTokShops table");
            return AccountDailyMetricResult.Unavailable(
                "Kredensial TikTok belum tersedia (token belum di-exchange) - integrasi analytics level akun belum aktif. " +
                "Selesaikan otorisasi TikTok (OAuth) sekali di environment Development ini untuk membuat baris kredensial.");
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
            "Daily Report overview source: shop {ShopCipher} (cipher source: {CipherSource}), requesting {Route} ({Start}..{EndExclusive})",
            shopCipher,
            shopCipherFromConfig ? "TikTok:DefaultShopCipher config" : "TikTokShops table",
            EndpointPath,
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

        // Development-only raw response dump (same pattern as TikTokVideoService's
        // details/performance samples): lets the operator verify the LIVE response
        // schema - which fields this endpoint really returns - without logging any
        // token, signature or request payload. The response body never carries the
        // access token (it travels only in the request header), and the file is
        // written outside wwwroot (App_Data), so it is not served.
        try
        {
            if (_environment is not null && _environment.IsDevelopment() && !string.IsNullOrWhiteSpace(content))
            {
                var sampleDir = System.IO.Path.Combine(AppContext.BaseDirectory, "App_Data");
                System.IO.Directory.CreateDirectory(sampleDir);
                var sampleFile = System.IO.Path.Combine(
                    sampleDir,
                    $"tiktok-video-overview-sample-{query.Start:yyyy-MM-dd}_{query.EndExclusive:yyyy-MM-dd}.json");
                await System.IO.File.WriteAllTextAsync(sampleFile, content, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Diagnostics must never break the report itself.
            _logger.LogDebug(ex, "Could not write TikTok overview response sample");
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

            // Period + DAILY interaction totals. PRIMARY path: the 202609 batch endpoint
            // with granularity=1D (verified live: per-day traffic likes/comments/shares,
            // ~6 requests for the whole catalog at 500 ids/request, cached per window).
            // FALLBACK: the paginated list totals (windowed, no daily breakdown) when the
            // batch path cannot answer - lookback rejection, business/HTTP error, rate
            // limit or schema mismatch. A partial daily sum is never presented as totals.
            var dailyInteractions = await FetchDailyInteractionsAsync(shopCipher, accessToken, query, cancellationToken);

            var interactions = dailyInteractions.IsSuccess
                ? new ListInteractionsOutcome(
                    dailyInteractions.Likes,
                    dailyInteractions.Comments,
                    dailyInteractions.Shares,
                    dailyInteractions.VideoCount,
                    true,
                    null)
                : await FetchPeriodInteractionsAsync(shopCipher, accessToken, query, cancellationToken);

            // Attach the per-day breakdown to the overview records so the daily table and
            // charts can show REAL daily Interactions. A period aggregate (list fallback)
            // is NEVER spread across days - those rows stay null (honest "-").
            if (dailyInteractions.IsSuccess && dailyInteractions.Daily is not null)
            {
                foreach (var record in records)
                {
                    if (dailyInteractions.Daily.TryGetValue(record.Date, out var dayTotals))
                    {
                        record.Likes = dayTotals.Likes;
                        record.Comments = dayTotals.Comments;
                        record.Shares = dayTotals.Shares;
                    }
                }
            }

            IReadOnlySet<string> availableKeys = interactions.Likes.HasValue
                && interactions.Comments.HasValue
                && interactions.Shares.HasValue
                ? new HashSet<string>(VerifiedMetricKeys, StringComparer.Ordinal) { DailyReportMetrics.Interactions }
                : VerifiedMetricKeys;

            return AccountDailyMetricResult.Available(new AccountDailyMetricDataset
            {
                Records = records
                    .OrderBy(r => r.Date)
                    .ToList(),
                FollowerSnapshots = Array.Empty<FollowerSnapshot>(),
                ViewerComposition = null, // unverified for this endpoint
                SourceName = ProviderSourceName,
                PeriodInteractions = interactions.IsSuccess
                    ? new AccountPeriodInteractions
                    {
                        Likes = interactions.Likes,
                        Comments = interactions.Comments,
                        Shares = interactions.Shares,
                        VideoCount = interactions.VideoCount
                    }
                    : null,
                AvailableMetricKeys = availableKeys
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
    // Period interaction totals from the paginated video LIST
    // (see ListEndpointPath: windowed per-video likes/comments/shares)
    // ------------------------------------------------------------------

    /// <summary>Outcome of aggregating one window over the list endpoint.</summary>
    private sealed record ListInteractionsOutcome(
        long? Likes,
        long? Comments,
        long? Shares,
        long? VideoCount,
        bool IsSuccess,
        string? Reason);

    /// <summary>
    /// Cache-aware wrapper around <see cref="FetchPeriodInteractionsCoreAsync"/>. Keyed
    /// per (shop, half-open window) so two windows of the same report never share a
    /// total. When no cache is registered (unit tests), behaviour is unchanged.
    /// </summary>
    private async Task<ListInteractionsOutcome> FetchPeriodInteractionsAsync(
        string shopCipher,
        string accessToken,
        AccountDailyMetricQuery query,
        CancellationToken cancellationToken)
    {
        var startIso = query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endIso = query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (_interactionsCache is not null)
        {
            var cacheKey = shopCipher + "|" + startIso + "|" + endIso;
            if (_interactionsCache.TryGet(cacheKey, out var cached))
            {
                _logger.LogInformation(
                    "TikTok list interactions served from cache for window {Start}..{End} (no TikTok list requests made)",
                    startIso, endIso);
                return new ListInteractionsOutcome(
                    cached.Likes, cached.Comments, cached.Shares, cached.VideoCount, cached.IsSuccess, null);
            }

            var outcome = await FetchPeriodInteractionsCoreAsync(
                shopCipher, accessToken, query, cancellationToken);
            _interactionsCache.Set(cacheKey, new TikTokPeriodInteractionsCache.CachedTotals(
                outcome.IsSuccess, outcome.Likes, outcome.Comments, outcome.Shares, outcome.VideoCount));
            return outcome;
        }

        return await FetchPeriodInteractionsCoreAsync(shopCipher, accessToken, query, cancellationToken);
    }

    /// <summary>
    /// One cheap request to the paginated LIST endpoint (page 1) for the API's catalog
    /// total. Tolerant of every observed pagination shape (data.total_count, data.total,
    /// data.page_info.total, numeric or numeric-string). Returns null when no total is
    /// exposed or the page-1 request itself fails - the caller then skips the
    /// completeness cross-check (documented) and the main list path reports its own
    /// honest errors if it runs.
    /// </summary>
    private async Task<int?> FetchListCatalogTotalAsync(
        string shopCipher,
        string accessToken,
        AccountDailyMetricQuery query,
        CancellationToken cancellationToken)
    {
        var queryDict = new Dictionary<string, string?>
        {
            { "app_key", _options.AppKey },
            { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
            { "shop_cipher", shopCipher },
            { "start_date_ge", query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            { "end_date_lt", query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            { "page_size", ListPageSize.ToString(CultureInfo.InvariantCulture) }
        };

        var signInput = queryDict.Where(kv => !string.IsNullOrEmpty(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var sign = _signatureService.GenerateSignature("GET", ListEndpointPath, signInput, null, null);
        signInput.Add("sign", sign);
        var url = QueryHelpers.AddQueryString(ListEndpointPath, signInput);

        try
        {
            var client = _httpFactory.CreateClient("TikTokApi");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-tts-access-token", accessToken);
            req.Headers.Add("Accept", "application/json");
            using var res = await client.SendAsync(req, cancellationToken);
            if ((int)res.StatusCode != 200)
            {
                return null;
            }

            var content = await res.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (ReadInt32Flexible(root, "code") ?? 0) != 0
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var total = ReadInt32Flexible(data, "total_count")
                ?? ReadInt32Flexible(data, "total");
            if (total is null && data.TryGetProperty("page_info", out var pageInfo) && pageInfo.ValueKind == JsonValueKind.Object)
            {
                total = ReadInt32Flexible(pageInfo, "total") ?? ReadInt32Flexible(pageInfo, "total_count");
            }

            return total;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the list API catalog total for the daily-interactions completeness check");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // DAILY interaction breakdown via the 202609 batch endpoint
    // (see BatchEndpointPath: per-video daily intervals with traffic likes/comments/shares)
    // ------------------------------------------------------------------

    /// <summary>Outcome of aggregating one window over the batch endpoint.</summary>
    private sealed record DailyInteractionsOutcome(
        bool IsSuccess,
        long? Likes,
        long? Comments,
        long? Shares,
        long? VideoCount,
        IReadOnlyDictionary<DateTime, (long? Likes, long? Comments, long? Shares)>? Daily,
        string? Reason);

    /// <summary>
    /// Cache-aware wrapper around <see cref="FetchDailyInteractionsCoreAsync"/>.
    /// Keyed per (shop, half-open window) under a BATCH-specific prefix so a cached
    /// list-based total never masks a batch outcome (and vice versa). Negative outcomes
    /// are cached too, so a struggling API is not hammered by repeated renders.
    /// </summary>
    private async Task<DailyInteractionsOutcome> FetchDailyInteractionsAsync(
        string shopCipher,
        string accessToken,
        AccountDailyMetricQuery query,
        CancellationToken cancellationToken)
    {
        var startIso = query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endIso = query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (_interactionsCache is not null)
        {
            var cacheKey = "batch|" + shopCipher + "|" + startIso + "|" + endIso;
            if (_interactionsCache.TryGet(cacheKey, out var cached))
            {
                _logger.LogInformation(
                    "TikTok batch daily interactions served from cache for window {Start}..{End} (no TikTok batch requests made)",
                    startIso, endIso);
                return new DailyInteractionsOutcome(
                    cached.IsSuccess,
                    cached.Likes,
                    cached.Comments,
                    cached.Shares,
                    cached.VideoCount,
                    cached.Daily is null
                        ? null
                        : cached.Daily.ToDictionary(d => d.Date, d => (d.Likes, d.Comments, d.Shares)),
                    null);
            }

            var outcome = await FetchDailyInteractionsCoreAsync(shopCipher, accessToken, query, cancellationToken);
            _interactionsCache.Set(cacheKey, new TikTokPeriodInteractionsCache.CachedTotals(
                outcome.IsSuccess,
                outcome.Likes,
                outcome.Comments,
                outcome.Shares,
                outcome.VideoCount,
                outcome.Daily is null
                    ? null
                    : outcome.Daily
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new TikTokPeriodInteractionsCache.CachedDaily(
                            kv.Key, kv.Value.Likes, kv.Value.Comments, kv.Value.Shares))
                        .ToList()));
            return outcome;
        }

        return await FetchDailyInteractionsCoreAsync(shopCipher, accessToken, query, cancellationToken);
    }

    /// <summary>
    /// Aggregates PER-DAY likes/comments/shares from the 202609 batch endpoint
    /// (<see cref="BatchEndpointPath"/>, granularity=1D) over the local video catalog
    /// (ContentLogs - no extra TikTok list request for the ids).
    ///
    /// Guarantees (covered by unit tests):
    ///  - CHUNKED FAN-OUT: at most <see cref="BatchChunkSize"/> video ids per request,
    ///    bounded by <see cref="BatchMaxVideos"/>; anything else degrades to the
    ///    list-based totals instead of an unbounded fan-out.
    ///  - NO DOUBLE COUNTING: a HashSet of video ids deduplicates across chunks, so a
    ///    replayed id contributes exactly once.
    ///  - DAILY, NOT SPREAD: values are read from each video's DAILY intervals and
    ///    summed per day - a period aggregate is never distributed across dates.
    ///  - NULL != 0: a traffic field that never appears on any video keeps the whole
    ///    metric unavailable; explicit 0 values are counted as real zeros.
    ///  - ALL-OR-NOTHING: any chunk failure (HTTP, rate limit, business error, lookback
    ///    rejection, schema mismatch) fails the WHOLE daily outcome - the caller then
    ///    falls back to the list-based period totals; a partial daily sum is never shown.
    /// </summary>
    private async Task<DailyInteractionsOutcome> FetchDailyInteractionsCoreAsync(
        string shopCipher,
        string accessToken,
        AccountDailyMetricQuery query,
        CancellationToken cancellationToken)
    {
        var startIso = query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endIso = query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (_videoService is null)
        {
            // Unit-test construction without the batch service: list path remains the
            // only interactions source (behaviour identical to before this extension).
            return new DailyInteractionsOutcome(false, null, null, null, null, null,
                "Interactions harian tidak diminta: batch video service tidak terdaftar.");
        }

        // Local catalog ids: only videos posted BEFORE the window end can have
        // interactions inside the window. No TikTok request is spent on the id list.
        var videoIds = await _db.ContentLogs.AsNoTracking()
            .Where(x => x.VideoId != null && x.VideoId != ""
                && (x.VideoPostTime == null || x.VideoPostTime < query.EndExclusive))
            .Select(x => x.VideoId!)
            .Distinct()
            .Take(BatchMaxVideos)
            .ToListAsync(cancellationToken);

        if (videoIds.Count == 0)
        {
            // An EMPTY local catalog is only trusted when the API agrees it is empty:
            // a catalog the API knows about (total > 0) means our local sync is behind,
            // and reporting 0 interactions for a shop with thousands of videos would be
            // a fabricated total. Fall back to the list path whenever a total is exposed.
            var emptyCatalogTotal = await FetchListCatalogTotalAsync(shopCipher, accessToken, query, cancellationToken);
            if (emptyCatalogTotal is > 0)
            {
                _logger.LogWarning(
                    "TikTok batch daily interactions: local catalog is empty but the list API reports {Total} videos for window {Start}..{End} - falling back to list totals",
                    emptyCatalogTotal.Value, startIso, endIso);
                return new DailyInteractionsOutcome(false, null, null, null, null, null,
                    "Interactions harian tidak tersedia: katalog lokal kosong padahal API melaporkan " +
                    $"{emptyCatalogTotal.Value} video - sinkronisasi katalog tertinggal.");
            }

            // Verified empty catalog (API total 0 or not exposed): every day of the
            // window truly had 0 interactions.
            var zeroDaily = new Dictionary<DateTime, (long? Likes, long? Comments, long? Shares)>();
            for (var day = query.Start; day < query.EndExclusive; day = day.AddDays(1))
            {
                zeroDaily[day] = (0, 0, 0);
            }

            _logger.LogInformation(
                "TikTok batch daily interactions: empty local catalog for window {Start}..{End} - totals 0 from 0 videos",
                startIso, endIso);
            return new DailyInteractionsOutcome(true, 0, 0, 0, 0, zeroDaily, null);
        }

        var daily = new Dictionary<DateTime, (long? Likes, long? Comments, long? Shares)>();
        var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);
        long likes = 0, comments = 0, shares = 0;
        var likesSeen = false;
        var commentsSeen = false;
        var sharesSeen = false;

        var chunks = videoIds.Chunk(BatchChunkSize).ToList();

        // VERIFIED window-span constraint (probes 2026-10-22): with granularity=1D the
        // batch endpoint accepts windows of at most SEVEN days (8-day span -> HTTP 400
        // "Invalid Parameter"; an old 7-day window returned code=0, so POSITION does not
        // matter - only the span). Longer report windows are split into <=7-day
        // sub-windows; the total fan-out is bounded by BatchMaxFanOutRequests and
        // anything beyond that budget degrades to the list-based period totals.
        var subWindows = new List<(DateTime Start, DateTime EndExclusive)>();
        for (var windowStart = query.Start; windowStart < query.EndExclusive; windowStart = windowStart.AddDays(BatchMaxWindowDays))
        {
            var windowEnd = windowStart.AddDays(BatchMaxWindowDays);
            if (windowEnd > query.EndExclusive)
            {
                windowEnd = query.EndExclusive;
            }

            subWindows.Add((windowStart, windowEnd));
        }

        var totalRequests = chunks.Count * subWindows.Count;
        if (totalRequests > BatchMaxFanOutRequests)
        {
            _logger.LogInformation(
                "TikTok batch daily interactions: fan-out of {Requests} requests ({Chunks} chunks x {SubWindows} sub-windows) exceeds the budget of {Budget} for window {Start}..{End} - falling back to list totals",
                totalRequests, chunks.Count, subWindows.Count, BatchMaxFanOutRequests, startIso, endIso);
            return new DailyInteractionsOutcome(false, null, null, null, null, null,
                $"Interactions harian tidak tersedia: jendela terlalu lebar untuk batch harian ({totalRequests} request melebihi budget {BatchMaxFanOutRequests}).");
        }

        foreach (var (subStart, subEnd) in subWindows)
        {
            // Identity dedupe is PER SUB-WINDOW: the same video legitimately appears in
            // several sub-windows with different daily values. seenVideoIds keeps the
            // global unique count for reporting.
            var seenInSubWindow = new HashSet<string>(StringComparer.Ordinal);

            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                TikTokBatchPerformanceResult response;
                try
                {
                    response = await _videoService.FetchBatchPerformanceAsync(
                        chunks[chunkIndex], shopCipher, subStart, subEnd, "1D", cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "TikTok batch daily interactions: chunk {Chunk}/{Chunks} of sub-window {SubStart}..{SubEnd} failed (transport) for window {Start}..{End} - daily breakdown unavailable, falling back to list totals",
                        chunkIndex + 1, chunks.Count, subStart.ToString("yyyy-MM-dd"), subEnd.ToString("yyyy-MM-dd"), startIso, endIso);
                    return new DailyInteractionsOutcome(false, null, null, null, null, null,
                        $"Interactions harian tidak tersedia: request batch gagal (chunk {chunkIndex + 1}/{chunks.Count}).");
                }

                if (response.StatusCode == 429)
                {
                    _logger.LogWarning(
                        "TikTok batch daily interactions: rate-limited (429) on chunk {Chunk}/{Chunks} of sub-window {SubStart}..{SubEnd} for window {Start}..{End} - falling back to list totals",
                        chunkIndex + 1, chunks.Count, subStart.ToString("yyyy-MM-dd"), subEnd.ToString("yyyy-MM-dd"), startIso, endIso);
                    return new DailyInteractionsOutcome(false, null, null, null, null, null,
                        $"Interactions harian tidak tersedia: rate limit (429) pada chunk {chunkIndex + 1}/{chunks.Count}.");
                }

                if (response.StatusCode != 200 || response.Code is null or not 0)
                {
                    var lookback = response.Code == BatchLookbackErrorCode
                        || (response.Message?.Contains("lookback window", StringComparison.OrdinalIgnoreCase) ?? false);
                    _logger.LogWarning(
                        "TikTok batch daily interactions: chunk {Chunk}/{Chunks} of sub-window {SubStart}..{SubEnd} rejected (HTTP {Status}, code {Code}: {Message}, request_id {RequestId}, lookback {Lookback}) for window {Start}..{End} - falling back to list totals",
                        chunkIndex + 1, chunks.Count, subStart.ToString("yyyy-MM-dd"), subEnd.ToString("yyyy-MM-dd"),
                        response.StatusCode, response.Code, response.Message, response.RequestId, lookback, startIso, endIso);
                    return new DailyInteractionsOutcome(false, null, null, null, null, null,
                        lookback
                            ? $"Interactions harian tidak tersedia: batch API menolak jendela di luar lookback (code {response.Code})."
                            : $"Interactions harian tidak tersedia: batch API merespons HTTP {response.StatusCode}" +
                              (response.Code is null ? "." : $" (code {response.Code}: {response.Message})."));
                }

                foreach (var entry in response.VideoEntries)
                {
                    var videoId = entry.GetPropertyOrDefault("video_id");
                    if (string.IsNullOrWhiteSpace(videoId)
                        && entry.TryGetProperty("video", out var videoObj)
                        && videoObj.ValueKind == JsonValueKind.Object)
                    {
                        videoId = videoObj.GetPropertyOrDefault("id");
                    }

                    if (string.IsNullOrWhiteSpace(videoId))
                    {
                        continue; // no identity -> cannot guarantee "counted once"; skipped, never guessed
                    }

                    if (!seenInSubWindow.Add(videoId))
                    {
                        continue; // replayed id within this sub-window: no double counting
                    }

                    seenVideoIds.Add(videoId); // global unique count for reporting only

                    if (!entry.TryGetProperty("performance", out var performance)
                        || performance.ValueKind != JsonValueKind.Object
                        || !performance.TryGetProperty("intervals", out var intervals)
                        || intervals.ValueKind != JsonValueKind.Array)
                    {
                        continue; // per-video no-data (or no performance object): contributes nothing
                    }

                    foreach (var interval in intervals.EnumerateArray())
                    {
                        if (interval.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var start = ParseDate(interval.GetPropertyOrDefault("start_date"));
                        if (start is null || start.Value < query.Start || start.Value >= query.EndExclusive)
                        {
                            continue; // outside the half-open window
                        }

                        // A daily (1D) interval must be exactly one day; a multi-day interval
                        // cannot be attributed to a single day and is skipped, never split.
                        var end = ParseDate(interval.GetPropertyOrDefault("end_date"));
                        if (end is not null && end.Value != start.Value.AddDays(1))
                        {
                            continue;
                        }

                        if (!interval.TryGetProperty("traffic", out var traffic)
                            || traffic.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var v = ReadInt64Flexible(traffic, "likes");
                        var c = ReadInt64Flexible(traffic, "comments");
                        var s = ReadInt64Flexible(traffic, "shares");
                        // Per-day null != 0: a field ABSENT on every video of that day stays
                        // null for the day (honest "-"); only explicitly returned values (incl.
                        // real zeros) accumulate.
                        var existing = daily.GetValueOrDefault(start.Value);
                        daily[start.Value] = (
                            v is null ? existing.Likes : (existing.Likes ?? 0) + v.Value,
                            c is null ? existing.Comments : (existing.Comments ?? 0) + c.Value,
                            s is null ? existing.Shares : (existing.Shares ?? 0) + s.Value);
                        if (v.HasValue) { likes += v.Value; likesSeen = true; }
                        if (c.HasValue) { comments += c.Value; commentsSeen = true; }
                        if (s.HasValue) { shares += s.Value; sharesSeen = true; }
                    }
                }
            }
        }

        if (!likesSeen && !commentsSeen && !sharesSeen)
        {
            // No video carried the verified traffic fields - a schema mismatch, NOT a
            // window with zero interactions.
            _logger.LogWarning(
                "TikTok batch daily interactions carried no traffic likes/comments/shares on {Chunks} chunk(s) for window {Start}..{End} - daily breakdown unavailable",
                chunks.Count, startIso, endIso);
            return new DailyInteractionsOutcome(false, null, null, null, null, null,
                "Interactions harian tidak tersedia: respons batch tidak memuat field traffic likes/comments/shares.");
        }

        // Completeness cross-check (catalog coverage): the batch path can only sum the
        // videos the LOCAL catalog knows. When the list API reports its catalog total on
        // page 1 and fewer ids came back than that total, the local catalog is behind and
        // the totals would UNDERSTATE the window - fall back to the authoritative list
        // path instead of presenting a partial sum as the period total. When the API does
        // not expose a total, the check is skipped and logged (documented limitation).
        var catalogTotal = await FetchListCatalogTotalAsync(shopCipher, accessToken, query, cancellationToken);
        if (catalogTotal is not null && seenVideoIds.Count < catalogTotal.Value)
        {
            _logger.LogWarning(
                "TikTok batch daily interactions: local catalog returned {Seen} video ids but the list API reports {Total} - daily breakdown unusable, falling back to list totals",
                seenVideoIds.Count, catalogTotal.Value);
            return new DailyInteractionsOutcome(false, null, null, null, null, null,
                $"Interactions harian tidak tersedia: katalog lokal ({seenVideoIds.Count} video) lebih kecil dari total katalog API ({catalogTotal.Value}).");
        }

        if (catalogTotal is null)
        {
            _logger.LogInformation(
                "TikTok batch daily interactions: list API did not expose a catalog total for window {Start}..{End} - completeness cross-check skipped ({Seen} ids summed)",
                startIso, endIso, seenVideoIds.Count);
        }

        _logger.LogInformation(
            "TikTok batch daily interactions summed: window {Start}..{End}, chunks {Chunks}, sub-windows {SubWindows}, unique videos {Videos}, days {Days}, likes {Likes}, comments {Comments}, shares {Shares}",
            startIso, endIso, chunks.Count, subWindows.Count, seenVideoIds.Count, daily.Count,
            likesSeen ? likes : -1, commentsSeen ? comments : -1, sharesSeen ? shares : -1);

        return new DailyInteractionsOutcome(
            true,
            likesSeen ? likes : null,
            commentsSeen ? comments : null,
            sharesSeen ? shares : null,
            seenVideoIds.Count,
            daily,
            null);
    }

    /// <summary>
    /// Sums windowed per-video likes/comments/shares over ALL pages of
    /// <see cref="ListEndpointPath"/> for the exact half-open window of <paramref name="query"/>.
    ///
    /// Guarantees (covered by unit tests):
    ///  - COMPLETE PAGINATION: pages are followed via the server-provided next page token
    ///    until it is absent, bounded by <see cref="ListMaxPages"/>; hitting the guard reports
    ///    UNAVAILABLE rather than a partial sum presented as a period total.
    ///  - NO DOUBLE COUNTING: a HashSet of video ids deduplicates across pages, so every
    ///    video contributes exactly once even if a token were ever replayed.
    ///  - NULL != 0: a field that never appears on any video stays null (metric unavailable);
    ///    an explicit 0 from the API is a valid, counted zero.
    ///  - SAFE LOGGING: only codes/messages/page counts are logged - no token, no signature,
    ///    no payload.
    /// </summary>
    private async Task<ListInteractionsOutcome> FetchPeriodInteractionsCoreAsync(
        string shopCipher,
        string accessToken,
        AccountDailyMetricQuery query,
        CancellationToken cancellationToken)
    {
        var startIso = query.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endIso = query.EndExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);
        long likes = 0, comments = 0, shares = 0;
        var likesSeen = false;
        var commentsSeen = false;
        var sharesSeen = false;

        string? pageToken = null;
        var pages = 0;
        var rateLimitRetried = false;

        var client = _httpFactory.CreateClient("TikTokApi");

        while (pages < ListMaxPages)
        {
            pages++;

            var queryDict = new Dictionary<string, string?>
            {
                { "app_key", _options.AppKey },
                { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
                { "shop_cipher", shopCipher },
                { "start_date_ge", startIso },
                { "end_date_lt", endIso },
                { "page_size", ListPageSize.ToString(CultureInfo.InvariantCulture) }
            };
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                queryDict["page_token"] = pageToken;
            }

            var signInput = queryDict.Where(kv => !string.IsNullOrEmpty(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var sign = _signatureService.GenerateSignature("GET", ListEndpointPath, signInput, null, null);
            signInput.Add("sign", sign);
            var url = QueryHelpers.AddQueryString(ListEndpointPath, signInput);

            string content;
            System.Net.HttpStatusCode httpStatus;

            // Bounded TRANSPORT retry per page (fresh timestamp + signature per attempt),
            // same policy as the overview call above: only network failures are retried.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Add("x-tts-access-token", accessToken);
                    req.Headers.Add("Accept", "application/json");

                    using var res = await client.SendAsync(req, cancellationToken);
                    httpStatus = res.StatusCode;
                    content = await res.Content.ReadAsStringAsync(cancellationToken);

                    // Rate limit (429): ONE bounded, Retry-After-aware wait for this page -
                    // the shared app-group limit is real (observed live 2026-10-22) and must
                    // be honored instead of hammering.
                    if ((int)httpStatus == 429 && !rateLimitRetried)
                    {
                        rateLimitRetried = true;
                        var delaySeconds = res.Headers.RetryAfter?.Delta?.TotalSeconds is > 0 and <= 10
                            ? (int)Math.Ceiling(res.Headers.RetryAfter.Delta.Value.TotalSeconds)
                            : 1;
                        _logger.LogWarning(
                            "TikTok video list rate-limited (429) on page {Page} of {Route} - waiting {Seconds}s once",
                            pages, ListEndpointPath, delaySeconds);
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                        continue;
                    }

                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < MaxTransportAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "TikTok video list transport failure on page {Page} of {Route}, attempt {Attempt}/{Max} - retrying",
                        pages, ListEndpointPath, attempt, MaxTransportAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(TransportRetryBaseDelaySeconds * attempt), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "TikTok video list transport failure on page {Page} of {Route} after {Max} attempts",
                        pages, ListEndpointPath, MaxTransportAttempts);
                    return new ListInteractionsOutcome(null, null, null, null, false,
                        "Interactions tidak tersedia: jaringan/transport ke video list API gagal (halaman " + pages + ").");
                }
            }

            // HTTP-level failure: honest reason, never a partial total.
            if ((int)httpStatus < 200 || (int)httpStatus >= 300)
            {
                var statusForReason = (int)httpStatus == 429 ? "rate limit (429)" : $"HTTP {(int)httpStatus}";
                _logger.LogWarning(
                    "TikTok video list {Status} on page {Page} of {Route} (window {Start}..{End}) - interactions unavailable",
                    statusForReason, pages, ListEndpointPath, startIso, endIso);
                return new ListInteractionsOutcome(null, null, null, null, false,
                    $"Interactions tidak tersedia: video list API merespons {statusForReason} (halaman {pages}).");
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new ListInteractionsOutcome(null, null, null, null, false,
                        "Interactions tidak tersedia: respons video list API bukan objek JSON.");
                }

                var code = ReadInt32Flexible(root, "code") ?? 0;
                if (code != 0)
                {
                    var message = root.GetPropertyOrDefault("message");
                    var requestId = root.GetPropertyOrDefault("request_id");
                    _logger.LogWarning(
                        "TikTok video list business error: code {Code}, message {Message}, request_id {RequestId} on page {Page}",
                        code, message, requestId, pages);
                    return new ListInteractionsOutcome(null, null, null, null, false,
                        $"Interactions tidak tersedia: video list API menolak permintaan (code {code}: {message ?? "no message"}, request_id={requestId ?? "-"}).");
                }

                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    return new ListInteractionsOutcome(null, null, null, null, false,
                        "Interactions tidak tersedia: respons video list API tidak berisi objek data.");
                }

                var videoFieldSeenOnPage = false;
                var videosArrayPresent = data.TryGetProperty("videos", out var videos)
                    && videos.ValueKind == JsonValueKind.Array;
                if (videosArrayPresent)
                {
                    foreach (var video in videos.EnumerateArray())
                    {
                        if (video.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var videoId = video.GetPropertyOrDefault("id") ?? video.GetPropertyOrDefault("video_id");
                        if (string.IsNullOrWhiteSpace(videoId))
                        {
                            continue; // no identity -> cannot guarantee "counted once"; skipped, never guessed
                        }

                        if (!seenVideoIds.Add(videoId))
                        {
                            continue; // already summed on an earlier page: no double counting
                        }

                        videoFieldSeenOnPage = true;
                        var v = ReadInt64Flexible(video, "likes");
                        var c = ReadInt64Flexible(video, "comments");
                        var s = ReadInt64Flexible(video, "shares");
                        if (v.HasValue) { likes += v.Value; likesSeen = true; }
                        if (c.HasValue) { comments += c.Value; commentsSeen = true; }
                        if (s.HasValue) { shares += s.Value; sharesSeen = true; }
                    }
                }

                if (!videosArrayPresent)
                {
                    // A response without the verified videos array is a schema mismatch,
                    // NOT an empty window: it must never become a fabricated zero total.
                    return new ListInteractionsOutcome(null, null, null, null, false,
                        "Interactions tidak tersedia: respons video list API tidak memuat array videos.");
                }

                if (!videoFieldSeenOnPage && pages == 1)
                {
                    // Verified empty videos array on the first page: the window has no
                    // videos at all, so 0 (from 0 videos) is a real, complete total.
                    _logger.LogInformation(
                        "TikTok video list returned no videos for window {Start}..{End} - interactions total 0 from 0 videos",
                        startIso, endIso);
                    return new ListInteractionsOutcome(0, 0, 0, 0, true, null);
                }

                // Server-provided next page token (same variants the list sync accepts).
                string? next = data.GetPropertyOrDefault("next_page_token") ?? data.GetPropertyOrDefault("page_token");
                if (next == null && data.TryGetProperty("page_info", out var pageInfo) && pageInfo.ValueKind == JsonValueKind.Object)
                {
                    next = pageInfo.GetPropertyOrDefault("next_page_token") ?? pageInfo.GetPropertyOrDefault("page_token");
                }

                if (string.IsNullOrWhiteSpace(next))
                {
                    // Last page reached: totals cover the window completely.
                    var uniqueVideos = seenVideoIds.Count;
                    if (!likesSeen && !commentsSeen && !sharesSeen)
                    {
                        // Schema never carried the fields - the metric is NOT zero, it is unavailable.
                        _logger.LogWarning(
                            "TikTok video list carried no likes/comments/shares fields on any of {Pages} page(s) - interactions unavailable",
                            pages);
                        return new ListInteractionsOutcome(null, null, null, uniqueVideos, false,
                            "Interactions tidak tersedia: respons video list API tidak memuat field likes/comments/shares.");
                    }

                    _logger.LogInformation(
                        "TikTok list interactions summed: window {Start}..{End}, pages {Pages}, unique videos {Videos}, likes {Likes}, comments {Comments}, shares {Shares}",
                        startIso, endIso, pages, uniqueVideos,
                        likesSeen ? likes : -1, commentsSeen ? comments : -1, sharesSeen ? shares : -1);

                    return new ListInteractionsOutcome(
                        likesSeen ? likes : null,
                        commentsSeen ? comments : null,
                        sharesSeen ? shares : null,
                        uniqueVideos,
                        true,
                        null);
                }

                pageToken = next;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "TikTok video list page {Page}: malformed JSON payload", pages);
                return new ListInteractionsOutcome(null, null, null, null, false,
                    "Interactions tidak tersedia: respons video list API bukan JSON yang valid.");
            }
        }

        // Pagination guard: a partial sum must never be presented as the period total.
        _logger.LogWarning(
            "TikTok video list exceeded the pagination guard of {MaxPages} pages for window {Start}..{End} - interactions unavailable (no partial sum returned)",
            ListMaxPages, startIso, endIso);
        return new ListInteractionsOutcome(null, null, null, null, false,
            $"Interactions tidak tersedia: pagination melewati batas {ListMaxPages} halaman - total parsial tidak ditampilkan.");
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

/// <summary>
/// In-memory TTL cache for the PERIOD interaction totals of the paginated video LIST
/// endpoint (<see cref="TikTokShopVideoOverviewMetricSource.ListEndpointPath"/>).
///
/// Why this exists (measured live 2026-10-22): the list endpoint returns the WHOLE
/// video catalog for ANY date window (total_count ~2.800 videos = ~28 pages at
/// page_size=100 per period), so one uncached Daily Report render costs ~57 TikTok
/// requests (current + previous period) against the shared app-group rate limit
/// where a real 429 has already been observed. A short TTL keeps a rapid succession
/// of page loads / date-picking honest and cheap:
///  - keyed per (shop cipher, start, end) - two windows NEVER share a total;
///  - NEGATIVE outcomes (business errors, HTTP errors, rate limits) are cached too,
///    so a struggling API is not hammered by repeated renders;
///  - TTL is short (see <see cref="TimeToLive"/>) - this is a request-cost cache for
///    one report page, never a long-lived source of truth.
/// Registered as a SINGLETON (the provider itself is scoped/per-request). Unit tests
/// construct the provider without it, so test behaviour stays deterministic.
/// </summary>
public sealed class TikTokPeriodInteractionsCache
{
    /// <summary>Cache lifetime of one (shop, window) interaction outcome.</summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(15);

    /// <summary>One cached day of the batch-based daily interaction breakdown (per-field null = field never present that day).</summary>
    public sealed record CachedDaily(DateTime Date, long? Likes, long? Comments, long? Shares);

    /// <summary>
    /// Plain cached shape - keeps provider internals private. Daily is non-null only for
    /// the batch endpoint outcome (list-based totals carry no daily breakdown).
    /// </summary>
    public sealed record CachedTotals(
        bool IsSuccess,
        long? Likes,
        long? Comments,
        long? Shares,
        long? VideoCount,
        IReadOnlyList<CachedDaily>? Daily = null);

    private sealed record Entry(CachedTotals Totals, DateTimeOffset CachedAtUtc);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _entries
        = new(StringComparer.Ordinal);

    /// <summary>Returns a non-expired cached outcome for the exact key, if any.</summary>
    public bool TryGet(string key, out CachedTotals totals)
    {
        if (_entries.TryGetValue(key, out var entry)
            && DateTimeOffset.UtcNow - entry.CachedAtUtc < TimeToLive)
        {
            totals = entry.Totals;
            return true;
        }

        totals = null!;
        return false;
    }

    /// <summary>Stores an outcome (success OR failure) for the exact key.</summary>
    public void Set(string key, CachedTotals totals)
        => _entries[key] = new Entry(totals, DateTimeOffset.UtcNow);
}
