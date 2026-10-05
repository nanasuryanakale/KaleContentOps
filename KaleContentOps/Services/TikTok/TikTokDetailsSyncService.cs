using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

// Dedicated service to sync per-video details and persist ContentMetrics.
public class TikTokDetailsSyncService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TikTokOptions _options;
    private readonly AppDbContext _db;
    private readonly ITikTokVideoService _videoService;
    private readonly Microsoft.Extensions.Logging.ILogger<TikTokDetailsSyncService> _logger;

    public TikTokDetailsSyncService(IHttpClientFactory httpFactory, Microsoft.Extensions.Options.IOptions<TikTokOptions> options, AppDbContext db, ITikTokVideoService videoService, Microsoft.Extensions.Logging.ILogger<TikTokDetailsSyncService>? logger = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _db = db;
        _videoService = videoService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TikTokDetailsSyncService>.Instance;
    }

    // Process a batch of content logs for a given shop. Limit controls number of videos processed.
    public async Task<int> RunDetailsSyncAsync(string shopCipher, int limit = 10, int skip = 0, CancellationToken cancellationToken = default)
    {
        // Resolve the TikTokShop ID from the cipher up-front with a simple query
        // This avoids forcing LEFT JOIN in the candidate query and allows efficient use of indexes
        var shop = await _db.TikTokShops
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ShopCipher == shopCipher, cancellationToken);

        if (shop == null)
        {
            // Invalid shop cipher: return 0 processed candidates
            return 0;
        }

        long shopId = shop.Id;

        // Select candidate ContentLogs: must have VideoId, must belong to this shop, filter by resolved shopId (not TikTokShop navigation)
        var query = _db.ContentLogs
            .AsNoTracking()
            .Where(cl => cl.TikTokShopId == shopId && !string.IsNullOrEmpty(cl.VideoId));

        // Determine candidate priority using latest ContentMetric per ContentLog
        // Priority: 0 = no metric, 1 = metric exists but NOT enriched (views-only), 2 = metric exists and enriched

        // Apply pagination while preserving priority semantics.
        // To avoid expensive server-side grouping over huge tables, execute prioritized queries in sequence
        // and apply skip/limit across the priority buckets so we only materialize the minimum rows.

        var results = new List<dynamic>();
        int remainingSkip = skip;
        int remainingLimit = limit;

        // Priority 0: ContentLogs with no metrics
        // Order priority 0 (no metric) by newest VideoPostTime first, tie-breaker Id DESC to prefer newer inserts
        var priority0Query = query.Where(cl => !_db.ContentMetrics.Any(m => m.ContentLogId == cl.Id))
            .OrderByDescending(cl => cl.VideoPostTime)
            .ThenByDescending(cl => cl.Id);
        if (remainingLimit > 0)
        {
            var p0 = await priority0Query.Skip(remainingSkip).Take(remainingLimit).ToListAsync(cancellationToken);
            results.AddRange(p0.Select(cl => new { Cl = cl, LatestCapturedAt = (DateTime?)null, IsEnriched = (bool?)null }));
            remainingSkip = Math.Max(0, remainingSkip - p0.Count);
            remainingLimit -= p0.Count;
        }

        // Prepare base content log ids for this shop to restrict subsequent queries
        var shopContentLogIds = query.Select(cl => cl.Id);

        // Helper: function to fetch next bucket (priority 1 = not enriched, priority 2 = enriched)
        async Task<List<(ContentLog Cl, DateTime? CapturedAt, bool IsEnriched)>> FetchPriorityBucketAsync(bool wantEnriched, int skipCount, int takeCount, CancellationToken ct)
        {
            // latest per log (restricted to this shop) -> join back to metrics
            var latestPerLogRestricted = _db.ContentMetrics
                .Where(m => shopContentLogIds.Contains(m.ContentLogId))
                .GroupBy(m => m.ContentLogId)
                .Select(g => new { ContentLogId = g.Key, CapturedAt = g.Max(x => x.CapturedAt) });

            var latestMetrics = from lm in latestPerLogRestricted
                                join m in _db.ContentMetrics on new { lm.ContentLogId, lm.CapturedAt } equals new { m.ContentLogId, m.CapturedAt }
                                where (m.Likes.HasValue || m.Comments.HasValue || m.Shares.HasValue || m.NewFollowers.HasValue || m.Reach.HasValue || m.AverageWatch.HasValue || m.FullWatchRate.HasValue || m.DemographicsJson != null) == wantEnriched
                                select new { lm.ContentLogId, m.CapturedAt };

            // Order by ContentLog.VideoPostTime DESC then Id DESC to prioritize newest videos within the same priority bucket
            var q = from cl in query
                    join lm in latestMetrics on cl.Id equals lm.ContentLogId
                    orderby cl.VideoPostTime descending, cl.Id descending
                    select new { Cl = cl, CapturedAt = (DateTime?)lm.CapturedAt };

            var page = await q.Skip(skipCount).Take(takeCount).ToListAsync(ct);
            return page.Select(x => ((ContentLog)x.Cl, x.CapturedAt, wantEnriched)).ToList();
        }

        // Priority 1: not enriched
        if (remainingLimit > 0)
        {
            var p1 = await FetchPriorityBucketAsync(false, remainingSkip, remainingLimit, cancellationToken);
            results.AddRange(p1.Select(t => new { Cl = t.Cl, LatestCapturedAt = t.CapturedAt, IsEnriched = (bool?)t.IsEnriched }));
            remainingSkip = Math.Max(0, remainingSkip - p1.Count);
            remainingLimit -= p1.Count;
        }

        // Priority 2: enriched
        if (remainingLimit > 0)
        {
            var p2 = await FetchPriorityBucketAsync(true, remainingSkip, remainingLimit, cancellationToken);
            results.AddRange(p2.Select(t => new { Cl = t.Cl, LatestCapturedAt = t.CapturedAt, IsEnriched = (bool?)t.IsEnriched }));
        }

        // Build candidates list expected by the remainder of the method
        var candidates = results.Select(r => new { Cl = (ContentLog)r.Cl, LatestMetric = (object?)null }).ToList();

        if (candidates.Count == 0)
        {
            _logger.LogInformation("Details sync: no candidate ContentLogs found for shop {ShopCipher}", shopCipher);
            return 0;
        }

        int processed = 0;

        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = await SyncOneContentLogAsync(entry.Cl, shopCipher, cancellationToken);
            if (info.Synced) processed++;
            if (info.ThrottleStop) break; // 429: stop the batch run to avoid further throttle
        }

        return processed;

        return processed;
    }

    // Result model for P1 batch runs. In-memory only, not persisted.
    public class P1DetailsResult
    {
        public int P0 { get; set; }
        public int P1Before { get; set; }
        public int P2 { get; set; }
        public int CandidatesSelected { get; set; }
        public int Attempted { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int NoEnrichment { get; set; }
        public int Throttled { get; set; }
        public int RateLimit36009002 { get; set; }
        public int NewContentMetrics { get; set; }
        public int DemographicsPopulated { get; set; }
        public int P1After { get; set; }
    }

    private class SyncOneResult
    {
        public bool Synced { get; set; }
        public bool ThrottleStop { get; set; }
        public bool NewMetricAdded { get; set; }
        public bool DemographicsPopulated { get; set; }
        public bool NoEnrichment { get; set; }
        public bool RateLimit36009002 { get; set; }
    }

    // Run a bounded details sync that targets only P1 candidates (latest metric exists but is not enriched).
    // This is a safe, idempotent, shop-scoped operation that reuses the existing per-video pipeline
    // and respects the same retry/backoff/throttle semantics. It will process up to `limit` videos
    // and stop on HTTP 429 / throttle signals. It does not touch P0 candidates.
    public async Task<P1DetailsResult> RunP1DetailsSyncAsync(string shopCipher, int limit = 10, int skip = 0, CancellationToken cancellationToken = default)
    {
        var shop = await _db.TikTokShops
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ShopCipher == shopCipher, cancellationToken);

        if (shop == null) return new P1DetailsResult();

        long shopId = shop.Id;

        var query = _db.ContentLogs
            .AsNoTracking()
            .Where(cl => cl.TikTokShopId == shopId && !string.IsNullOrEmpty(cl.VideoId));

        // latest per log restricted
        var latestPerLogRestricted = _db.ContentMetrics
            .Where(m => _db.ContentLogs.Where(cl => cl.TikTokShopId == shopId).Select(cl => cl.Id).Contains(m.ContentLogId))
            .GroupBy(m => m.ContentLogId)
            .Select(g => new { ContentLogId = g.Key, CapturedAt = g.Max(x => x.CapturedAt) });

        var latestMetrics = from lm in latestPerLogRestricted
                            join m in _db.ContentMetrics on new { lm.ContentLogId, lm.CapturedAt } equals new { m.ContentLogId, m.CapturedAt }
                            where (m.Likes.HasValue || m.Comments.HasValue || m.Shares.HasValue || m.NewFollowers.HasValue || m.Reach.HasValue || m.AverageWatch.HasValue || m.FullWatchRate.HasValue || m.DemographicsJson != null) == false
                            select new { lm.ContentLogId, m.CapturedAt };

        var q = from cl in query
                join lm in latestMetrics on cl.Id equals lm.ContentLogId
                orderby cl.VideoPostTime descending, cl.Id descending
                select new { Cl = cl, CapturedAt = (DateTime?)lm.CapturedAt };

        var page = await q.Skip(skip).Take(limit).ToListAsync(cancellationToken);

        var candidates = page.Select(x => (ContentLog)x.Cl).ToList();

        // Prepare result
        var result = new P1DetailsResult();

        // compute P0/P1/P2 counts for reporting (P1Before is the total P1 candidate count)
        try
        {
            result.P0 = await query.Where(cl => !_db.ContentMetrics.Any(m => m.ContentLogId == cl.Id)).CountAsync(cancellationToken);
        }
        catch { result.P0 = 0; }

        try
        {
            result.P1Before = await latestMetrics.CountAsync(cancellationToken);
        }
        catch { result.P1Before = 0; }

        try
        {
            // P2 = those with latest metric and enriched == true
            var latestPerLogRestricted2 = _db.ContentMetrics
                .Where(m => _db.ContentLogs.Where(cl => cl.TikTokShopId == shopId).Select(cl => cl.Id).Contains(m.ContentLogId))
                .GroupBy(m => m.ContentLogId)
                .Select(g => new { ContentLogId = g.Key, CapturedAt = g.Max(x => x.CapturedAt) });

            var latestEnriched = from lm in latestPerLogRestricted2
                                 join m in _db.ContentMetrics on new { lm.ContentLogId, lm.CapturedAt } equals new { m.ContentLogId, m.CapturedAt }
                                 where (m.Likes.HasValue || m.Comments.HasValue || m.Shares.HasValue || m.NewFollowers.HasValue || m.Reach.HasValue || m.AverageWatch.HasValue || m.FullWatchRate.HasValue || m.DemographicsJson != null) == true
                                 select lm.ContentLogId;

            result.P2 = await latestEnriched.Distinct().CountAsync(cancellationToken);
        }
        catch { result.P2 = 0; }

        result.CandidatesSelected = candidates.Count;

        foreach (var cl in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Attempted++;

            var info = await SyncOneContentLogAsync(cl, shop.ShopCipher, cancellationToken);

            if (info.ThrottleStop)
            {
                result.Throttled++;
                // Stop processing further candidates on throttle
                if (info.RateLimit36009002) result.RateLimit36009002++;
                break;
            }

            if (info.NewMetricAdded)
            {
                result.Succeeded++;
                result.NewContentMetrics += 1;
                if (info.DemographicsPopulated) result.DemographicsPopulated += 1;
            }
            else if (info.NoEnrichment)
            {
                result.NoEnrichment++;
            }
            else if (!info.NewMetricAdded && !info.NoEnrichment)
            {
                // considered a failed attempt (exception, non-200, etc.)
                result.Failed++;
            }

            if (info.RateLimit36009002) result.RateLimit36009002++;
        }

        // compute post-run P1 count
        try
        {
            result.P1After = await latestMetrics.CountAsync(cancellationToken);
        }
        catch { result.P1After = 0; }

        return result;
    }

    // Development-only: sync details for ONE existing ContentLog selected by VideoId.
    // Reuses the exact same pipeline as the batch flow (RunDetailsDiagnosticAsync -> ParseDetailsMetrics
    // -> MapDetailsMetricsToContentMetric -> SaveChangesAsync); no duplicate HTTP/parser/persistence logic.
    // ShopCipher is always resolved from the database (never accepted from the request).
    public async Task<bool> RunSingleVideoSyncAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var log = await _db.ContentLogs.AsNoTracking()
            .FirstOrDefaultAsync(cl => cl.VideoId == videoId, cancellationToken);

        if (log == null)
        {
            _logger.LogWarning("Details sync (single): no ContentLog found for VideoId {VideoId}", videoId);
            return false;
        }

        var shop = await _db.TikTokShops.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == log.TikTokShopId, cancellationToken);

        if (shop == null || string.IsNullOrWhiteSpace(shop.ShopCipher))
        {
            _logger.LogWarning("Details sync (single): ContentLog {ContentLogId} has no resolvable shop", log.Id);
            return false;
        }

            var info = await SyncOneContentLogAsync(log, shop.ShopCipher, cancellationToken);
            return info.Synced;
    }

    // Production-safe: sync details for ONE existing ContentLog selected by ContentLog.Id.
    // This is the minimal service entry point required by Admin UI to deterministically
    // target exactly one ContentLog. It reuses the shared SyncOneContentLogAsync pipeline.
    public async Task<bool> SyncSingleContentLogAsync(long contentLogId, CancellationToken cancellationToken = default)
    {
        var log = await _db.ContentLogs
            .AsNoTracking()
            .FirstOrDefaultAsync(cl => cl.Id == contentLogId, cancellationToken);

        if (log == null)
        {
            _logger.LogWarning("Details sync (single): no ContentLog found for Id {ContentLogId}", contentLogId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(log.VideoId))
        {
            _logger.LogWarning("Details sync (single): ContentLog {ContentLogId} has no VideoId", contentLogId);
            return false;
        }

        var shop = await _db.TikTokShops
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == log.TikTokShopId, cancellationToken);

        if (shop == null || string.IsNullOrWhiteSpace(shop.ShopCipher))
        {
            _logger.LogWarning("Details sync (single): ContentLog {ContentLogId} has no resolvable shop", contentLogId);
            return false;
        }

        var info = await SyncOneContentLogAsync(log, shop.ShopCipher, cancellationToken);
        return info.Synced;
    }

    // Shared per-video pipeline used by both the batch flow and the single-video (dev) flow.
    private async Task<SyncOneResult> SyncOneContentLogAsync(ContentLog log, string shopCipher, CancellationToken cancellationToken)
    {
        var videoId = log.VideoId!;
        var outInfo = new SyncOneResult();
        try
        {
            // Call via interface (implementation performs rate-limited HTTP calls with retry/backoff)
            var res = await _videoService.RunDetailsDiagnosticAsync(videoId, shopCipher, cancellationToken);

            if (res.StatusCode == 200 && res.Data.HasValue)
            {
                var d = res.Data.Value;
                // Use centralized parser from TikTokVideoService to extract metrics
                if (_videoService is TikTokVideoService concrete)
                {
                    var dm = concrete.ParseDetailsMetrics(d);
                    var metric = concrete.MapDetailsMetricsToContentMetric(dm, log.Id);

                    if (metric != null && (metric.Views.HasValue || metric.Likes.HasValue || metric.Comments.HasValue || metric.Shares.HasValue || metric.NewFollowers.HasValue || metric.Reach.HasValue || metric.AverageWatch.HasValue || metric.FullWatchRate.HasValue || !string.IsNullOrWhiteSpace(metric.DemographicsJson)))
                    {
                        _db.ContentMetrics.Add(metric);
                        await _db.SaveChangesAsync(cancellationToken);
                        outInfo.Synced = true;
                        outInfo.NewMetricAdded = true;
                        outInfo.DemographicsPopulated = !string.IsNullOrWhiteSpace(metric.DemographicsJson);
                        return outInfo;
                    }

                    _logger.LogDebug("Details sync: no metric fields returned for video {VideoId}", videoId);
                    outInfo.NoEnrichment = true;
                    return outInfo;
                }

                _logger.LogWarning("Details sync: video service is not concrete TikTokVideoService; skipping mapping for video {VideoId}", videoId);
                return outInfo;
            }

            if (res.StatusCode == 429)
            {
                _logger.LogWarning("Details sync: TikTok returned 429 for shop {ShopCipher}, stopping details sync run.", shopCipher);
                outInfo.ThrottleStop = true;
                return outInfo;
            }

                if (res.StatusCode == 200)
                {
                    // HTTP 200 but no data: TikTok business error (e.g. code 36009002 after retry
                    // exhaustion returns Data = null; video deleted/private returns empty data).
                    _logger.LogWarning("Details sync: business error (no data) for video {VideoId}", videoId);

                    // detect known rate-limit business code if present on root and treat as throttle stop
                    try
                    {
                        if (res.Root.HasValue && res.Root.Value.ValueKind == JsonValueKind.Object && res.Root.Value.TryGetProperty("code", out var codeElem) && codeElem.ValueKind == JsonValueKind.Number && codeElem.TryGetInt32(out var codeVal) && codeVal == 36009002)
                        {
                            outInfo.RateLimit36009002 = true;
                            outInfo.ThrottleStop = true; // propagate as throttle stop so batches halt
                        }
                    }
                    catch { }

                    return outInfo;
                }

            _logger.LogWarning("Details sync: non-200 response {Status} for video {VideoId}", res.StatusCode, videoId);
            return outInfo;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Details sync: error processing video {VideoId}", videoId);
            return outInfo;
        }
    }
}
