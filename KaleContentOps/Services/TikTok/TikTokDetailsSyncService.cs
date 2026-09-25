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
        // Select candidate ContentLogs: have VideoId, prefer missing metrics or not-yet-enriched metrics.
        var query = _db.ContentLogs
            .AsNoTracking()
            .Where(cl => cl.TikTokShop != null && cl.TikTokShop.ShopCipher == shopCipher && !string.IsNullOrEmpty(cl.VideoId));

        // Determine candidate priority using latest ContentMetric per ContentLog
        // Priority: 0 = no metric, 1 = metric exists but NOT enriched (views-only), 2 = metric exists and enriched

        // 1) Compute latest CapturedAt per ContentLog (server-side)
        var latestPerLog = _db.ContentMetrics
            .GroupBy(m => m.ContentLogId)
            .Select(g => new
            {
                ContentLogId = g.Key,
                CapturedAt = g.Max(x => x.CapturedAt)
            });

        // 2) Join back to ContentMetrics to obtain the latest metric row and compute IsEnriched (server-side)
        var latestRows = from lm in latestPerLog
                         join m in _db.ContentMetrics on new { lm.ContentLogId, lm.CapturedAt } equals new { m.ContentLogId, m.CapturedAt }
                         select new
                         {
                             m.ContentLogId,
                             m.CapturedAt,
                             IsEnriched = (m.Likes.HasValue || m.Comments.HasValue || m.Shares.HasValue || m.NewFollowers.HasValue || m.Reach.HasValue || m.AverageWatch.HasValue || m.FullWatchRate.HasValue || m.DemographicsJson != null)
                         };

        // 3) Left-join ContentLogs with latestRows so logs without metrics are included
        var candidateQuery = from cl in query
                             join lr in latestRows on cl.Id equals lr.ContentLogId into lj
                             from lr in lj.DefaultIfEmpty()
                             select new
                             {
                                 Cl = cl,
                                 LatestCapturedAt = (DateTime?)lr.CapturedAt,
                                 IsEnriched = (bool?)lr.IsEnriched
                             };

        // 4) Apply pagination while preserving priority semantics.
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
        var client = _httpFactory.CreateClient("TikTokApi");

        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var videoId = entry.Cl.VideoId!;

                try
                {
                    // Call via interface (implementation will perform rate-limited HTTP calls)
                    var res = await _videoService.RunDetailsDiagnosticAsync(videoId, shopCipher, cancellationToken);

                if (res.StatusCode == 200 && res.Data.HasValue)
                {
                    var d = res.Data.Value;
                    // Use centralized parser from TikTokVideoService to extract metrics
                    if (_videoService is TikTokVideoService concrete)
                    {
                        var dm = concrete.ParseDetailsMetrics(d);
                        var metric = concrete.MapDetailsMetricsToContentMetric(dm, entry.Cl.Id);

                        if (metric != null && (metric.Views.HasValue || metric.Likes.HasValue || metric.Comments.HasValue || metric.Shares.HasValue || metric.NewFollowers.HasValue || metric.Reach.HasValue || metric.AverageWatch.HasValue || metric.FullWatchRate.HasValue || !string.IsNullOrWhiteSpace(metric.DemographicsJson)))
                        {
                            _db.ContentMetrics.Add(metric);
                            await _db.SaveChangesAsync(cancellationToken);
                            processed++;
                        }
                        else
                        {
                            _logger.LogDebug("Details sync: no metric fields returned for video {VideoId}", videoId);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Details sync: video service is not concrete TikTokVideoService; skipping mapping for video {VideoId}", videoId);
                    }
                }
                else if (res.StatusCode == 429)
                {
                    _logger.LogWarning("Details sync: TikTok returned 429 for shop {ShopCipher}, stopping details sync run.", shopCipher);
                    break; // stop the batch run to avoid further throttle
                }
                else if (res.StatusCode == 200)
                {
                    // HTTP 200 but no data: TikTok business error (e.g. code 36009002 after retry
                    // exhaustion returns Data = null; video deleted/private returns empty data).
                    _logger.LogWarning("Details sync: business error (no data) for video {VideoId}", videoId);
                }
                else
                {
                    _logger.LogWarning("Details sync: non-200 response {Status} for video {VideoId}", res.StatusCode, videoId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Details sync: error processing video {VideoId}", videoId);
                // continue to next video
            }
        }

        return processed;
    }
}
