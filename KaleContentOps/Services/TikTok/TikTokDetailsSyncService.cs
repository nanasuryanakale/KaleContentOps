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
        // Select candidate ContentLogs: have VideoId, prefer missing metrics or stale metrics.
        var threshold = DateTime.UtcNow.AddDays(-1); // consider older than 1 day stale

        var query = _db.ContentLogs
            .AsNoTracking()
            .Where(cl => cl.TikTokShop != null && cl.TikTokShop.ShopCipher == shopCipher && !string.IsNullOrEmpty(cl.VideoId));

        // Order: missing metrics first, then oldest metric captured
        var candidates = query
            .Select(cl => new
            {
                Cl = cl,
                LatestMetricCaptured = _db.ContentMetrics.Where(m => m.ContentLogId == cl.Id).OrderByDescending(m => m.CapturedAt).Select(m => (DateTime?)m.CapturedAt).FirstOrDefault()
            })
            .OrderBy(x => x.LatestMetricCaptured.HasValue ? 1 : 0) // missing first
            .ThenBy(x => x.LatestMetricCaptured ?? DateTime.MinValue)
            .Skip(skip)
            .Take(limit)
            .ToList();

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
