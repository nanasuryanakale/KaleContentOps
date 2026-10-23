using System;
using System.Threading;
using System.Threading.Tasks;
using KaleContentOps.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KaleContentOps.Services.TikTok;

/// <summary>
/// Background service that drains the persistent TikTok batch-sync queue stored in SQL Server.
///
/// - Polls for a runnable job (Queued, or active whose row lease expired after a crash).
/// - Each poll creates a fresh DI scope (own AppDbContext), so no state lives in memory
///   and jobs survive application restarts.
/// - Jobs are claimed atomically by <see cref="TikTokBatchSyncService.TryClaimJobAsync"/>;
///   two workers can never process the same job/item.
/// - Poll interval comes from TikTok:BatchSyncPollSeconds (default 3s).
/// </summary>
public class TikTokBatchSyncWorker : BackgroundService
{
    private readonly ILogger<TikTokBatchSyncWorker> _logger;
    private readonly IServiceProvider _services;
    private readonly TikTokOptions _options;

    public TikTokBatchSyncWorker(
        ILogger<TikTokBatchSyncWorker> logger,
        IServiceProvider services,
        IOptions<TikTokOptions>? options = null)
    {
        _logger = logger;
        _services = services;
        _options = options?.Value ?? new TikTokOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TikTokBatchSyncWorker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var processedJob = false;

            try
            {
                using var scope = _services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<TikTokBatchSyncService>();
                var jobId = await service.ProcessNextJobAsync(stoppingToken);

                if (jobId.HasValue)
                {
                    processedJob = true;
                    _logger.LogInformation("TikTokBatchSyncWorker processed job {JobId}; looking for more work.", jobId.Value);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TikTokBatchSyncWorker cycle failed.");
            }

            if (!processedJob)
            {
                try
                {
                    var seconds = Math.Max(1, _options.BatchSyncPollSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("TikTokBatchSyncWorker stopping.");
    }
}
