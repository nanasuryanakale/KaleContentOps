using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using KaleContentOps.Data;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Services.TikTok;

public class TikTokDailySyncService : BackgroundService
{
    private readonly ILogger<TikTokDailySyncService> _logger;
    private readonly IServiceProvider _services;
    private readonly TikTokOptions _options;

    public TikTokDailySyncService(ILogger<TikTokDailySyncService> logger, IServiceProvider services, Microsoft.Extensions.Options.IOptions<TikTokOptions>? options = null)
    {
        _logger = logger;
        _services = services;
        _options = options?.Value ?? new TikTokOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TikTokDailySyncService starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Run once per day at approx 23:00 UTC local time or as soon as started in dev
                using var scope = _services.CreateScope();
                var videoSvc = scope.ServiceProvider.GetRequiredService<ITikTokVideoService>();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Iterate all authorized shops
                var shops = await db.TikTokShops.ToListAsync(stoppingToken);
                foreach (var s in shops)
                {
                    try
                    {
                        await videoSvc.FetchAndSaveVideoListAsync(s.ShopCipher ?? string.Empty, cancellationToken: stoppingToken);

                        // Details enrichment runs as part of the same routine sync workflow, gated behind
                        // TikTok:EnableDetails (existing flag for Details API calls) and a bounded batch size
                        // (TikTok:DetailsSyncBatchSize). RunDetailsSyncAsync goes through the shared
                        // TikTokGetWithRetryAsync path (Retry-After, exponential backoff with jitter,
                        // business code 36009002 handling) and the DetailsSemaphore concurrency limiter,
                        // so no additional retry/concurrency is introduced here.
                        if (_options.EnableDetails && _options.DetailsSyncBatchSize > 0)
                        {
                            var detailsSvc = scope.ServiceProvider.GetRequiredService<TikTokDetailsSyncService>();
                            var enriched = await detailsSvc.RunDetailsSyncAsync(
                                s.ShopCipher ?? string.Empty,
                                limit: _options.DetailsSyncBatchSize,
                                cancellationToken: stoppingToken);
                            _logger.LogInformation("Details sync for shop {ShopCipher} enriched {Processed} videos (batch size {BatchSize})", s.ShopCipher, enriched, _options.DetailsSyncBatchSize);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error syncing shop {ShopCipher}", s.ShopCipher);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in TikTokDailySyncService");
            }

            // Wait until next day (approx 24 hours) but respect cancellation
            try
            {
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("TikTokDailySyncService stopping.");
    }
}
