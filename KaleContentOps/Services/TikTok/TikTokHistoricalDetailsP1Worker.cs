using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Services.TikTok;

public class TikTokHistoricalDetailsP1Worker : BackgroundService
{
    private readonly ILogger<TikTokHistoricalDetailsP1Worker> _logger;
    private readonly IServiceProvider _services;
    private readonly TikTokOptions _options;
    private readonly SemaphoreSlim _cycleLock = new SemaphoreSlim(1, 1);

    public TikTokHistoricalDetailsP1Worker(ILogger<TikTokHistoricalDetailsP1Worker> logger, IServiceProvider services, IOptions<TikTokOptions> options)
    {
        _logger = logger;
        _services = services;
        _options = options?.Value ?? new TikTokOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Worker start
        _logger.LogInformation("TikTokHistoricalDetailsP1Worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_options.EnableHistoricalDetailsP1Scheduler)
            {
                _logger.LogDebug("Historical P1 scheduler disabled by configuration.");
                // Sleep for a minute and re-check configuration periodically
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Clamp batch size and interval to safe bounds
            var batchSize = Math.Min(Math.Max(1, _options.HistoricalDetailsP1BatchSize), 10);
            var intervalMinutes = Math.Max(5, _options.HistoricalDetailsP1IntervalMinutes);

            if (!await _cycleLock.WaitAsync(0, stoppingToken))
            {
                _logger.LogInformation("Previous historical P1 cycle still running; skipping this interval.");
                try { await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                _logger.LogInformation("Historical P1 cycle starting. BatchSize={BatchSize}", batchSize);

                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<KaleContentOps.Data.AppDbContext>();
                var detailsSvc = scope.ServiceProvider.GetRequiredService<TikTokDetailsSyncService>();

                var shops = await db.TikTokShops.ToListAsync(cancellationToken: stoppingToken);
                if (shops == null || shops.Count == 0)
                {
                    _logger.LogInformation("Historical P1 cycle: no authorized shops found.");
                }
                else
                {
                    foreach (var shop in shops)
                    {
                        if (stoppingToken.IsCancellationRequested) break;

                        if (string.IsNullOrWhiteSpace(shop.ShopCipher))
                        {
                            _logger.LogWarning("Historical P1 cycle: skipping shop {ShopId} due to empty ShopCipher", shop.Id);
                            continue;
                        }

                        _logger.LogInformation("Historical P1 processing shop {ShopCipher} with batch {BatchSize}", shop.ShopCipher, batchSize);

                        try
                        {
                            var result = await detailsSvc.RunP1DetailsSyncAsync(shop.ShopCipher, limit: batchSize, skip: 0, cancellationToken: stoppingToken);
                            _logger.LogInformation("Historical P1 shop {ShopCipher} processed Selected={Selected} Attempted={Attempted} NewContentMetrics={NewContentMetrics}", shop.ShopCipher, result?.CandidatesSelected ?? 0, result?.Attempted ?? 0, result?.NewContentMetrics ?? 0);
                            // If processed < batchSize, it may mean P1 exhausted or throttle occurred; rely on service logs for throttle detection
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogInformation("Historical P1 cycle canceled during shop processing.");
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Historical P1 shop {ShopCipher} error", shop.ShopCipher);
                        }
                    }
                }
            }
            finally
            {
                _cycleLock.Release();
            }

            _logger.LogInformation("Historical P1 cycle completed. Waiting {Minutes} minutes until next cycle.", _options.HistoricalDetailsP1IntervalMinutes);

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, _options.HistoricalDetailsP1IntervalMinutes)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("TikTokHistoricalDetailsP1Worker stopping.");
    }
}
