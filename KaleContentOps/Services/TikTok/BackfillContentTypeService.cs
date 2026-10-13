using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

/// <summary>
/// Service untuk backfill/reclassify ContentType untuk data lama yang belum memiliki
/// atau memiliki klasifikasi yang salah. Operasi ini aman dan idempotent:
/// - Hanya mengupdate ContentLogs yang sudah memiliki data sumber (IsLive, HasCommerce, atau ProductsJson)
/// - Tidak membuat klasifikasi fiktif jika informasi determinan tidak tersedia
/// - Dapat dijalankan berkali-kali tanpa menghasilkan duplikasi atau perubahan tidak perlu
/// </summary>
public class BackfillContentTypeService
{
    private readonly AppDbContext _db;
    private readonly Microsoft.Extensions.Logging.ILogger<BackfillContentTypeService> _logger;

    public BackfillContentTypeService(
        AppDbContext db,
        Microsoft.Extensions.Logging.ILogger<BackfillContentTypeService>? logger = null)
    {
        _db = db;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BackfillContentTypeService>.Instance;
    }

    /// <summary>
    /// Reclassify ContentType untuk semua ContentLogs yang memiliki potensi untuk diklasifikasi.
    /// Hanya mengupdate jika:
    /// 1. ContentTypeId belum diset (null), atau
    /// 2. ContentTypeId ada tetapi perlu update berdasarkan data baru (IsLive, HasCommerce, ProductsJson)
    /// 
    /// Operasi ini idempotent: dapat dijalankan berkali-kali tanpa efek samping.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple: (totalProcessed, updated, couldNotClassify)</returns>
    public async Task<(int TotalProcessed, int Updated, int CouldNotClassify)> BackfillAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting backfill of ContentType for all ContentLogs");

        int totalProcessed = 0;
        int updated = 0;
        int couldNotClassify = 0;
        const int batchSize = 100;

        var query = _db.ContentLogs.AsNoTracking()
            .Where(cl => !string.IsNullOrEmpty(cl.VideoId)); // Only valid logs

        var totalCount = await query.CountAsync(cancellationToken);
        _logger.LogInformation("Total ContentLogs to process: {Total}", totalCount);

        // Process in batches to avoid memory issues
        for (int skip = 0; skip < totalCount; skip += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await query
                .OrderBy(cl => cl.Id)
                .Skip(skip)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            foreach (var log in batch)
            {
                totalProcessed++;

                // Fetch latest ContentMetric for this log to get ProductsJson
                var latestMetric = await _db.ContentMetrics
                    .Where(m => m.ContentLogId == log.Id)
                    .OrderByDescending(m => m.CapturedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                var productsJson = latestMetric?.ProductsJson;

                // Determine if we can classify this log based on available signals
                bool hasIsLiveSignal = log.IsLive.HasValue;
                bool hasHasCommerceSignal = log.HasCommerce.HasValue;
                bool hasProductsSignal = !string.IsNullOrWhiteSpace(productsJson);

                // If no signals available, skip this log
                if (!hasIsLiveSignal && !hasHasCommerceSignal && !hasProductsSignal)
                {
                    couldNotClassify++;
                    _logger.LogDebug("ContentLog {LogId} (VideoId={VideoId}) has no classification signals (no IsLive, HasCommerce, or ProductsJson)", log.Id, log.VideoId);
                    continue;
                }

                // Classify based on available signals
                var classifiedTypeId = ContentTypeClassifier.ClassifyContentType(log.IsLive, log.HasCommerce, productsJson);

                // Update if needed
                bool shouldUpdate = false;
                if (!log.ContentTypeId.HasValue && classifiedTypeId.HasValue)
                {
                    shouldUpdate = true;
                    _logger.LogDebug("ContentLog {LogId} (VideoId={VideoId}) will be updated: ContentTypeId null -> {NewTypeId}", log.Id, log.VideoId, classifiedTypeId);
                }
                else if (log.ContentTypeId.HasValue && classifiedTypeId.HasValue && log.ContentTypeId != classifiedTypeId)
                {
                    shouldUpdate = true;
                    _logger.LogDebug("ContentLog {LogId} (VideoId={VideoId}) will be updated: ContentTypeId {OldTypeId} -> {NewTypeId}", log.Id, log.VideoId, log.ContentTypeId, classifiedTypeId);
                }

                if (shouldUpdate && classifiedTypeId.HasValue)
                {
                    // Fetch the tracked entity from DB again for update
                    var tracked = await _db.ContentLogs.FindAsync(new object[] { log.Id }, cancellationToken: cancellationToken);
                    if (tracked != null)
                    {
                        tracked.ContentTypeId = classifiedTypeId;
                        tracked.UpdatedAt = DateTime.UtcNow;
                        updated++;
                    }
                }
            }

            // Save batch updates
            if (updated > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation("Backfill progress: {Processed}/{Total} processed, {Updated} updated", 
                Math.Min(skip + batchSize, totalCount), totalCount, updated);
        }

        _logger.LogInformation("Backfill completed: {Total} processed, {Updated} updated, {CouldNotClassify} could not classify", 
            totalProcessed, updated, couldNotClassify);

        return (totalProcessed, updated, couldNotClassify);
    }

    /// <summary>
    /// Reclassify ContentType untuk ContentLogs dari shop tertentu.
    /// Berguna untuk testing atau backfill targeted.
    /// </summary>
    /// <param name="shopCipher">Shop cipher untuk filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple: (totalProcessed, updated, couldNotClassify)</returns>
    public async Task<(int TotalProcessed, int Updated, int CouldNotClassify)> BackfillShopAsync(string shopCipher, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting backfill of ContentType for shop {ShopCipher}", shopCipher);

        var shop = await _db.TikTokShops
            .FirstOrDefaultAsync(s => s.ShopCipher == shopCipher, cancellationToken);

        if (shop == null)
        {
            _logger.LogWarning("Shop {ShopCipher} not found", shopCipher);
            return (0, 0, 0);
        }

        int totalProcessed = 0;
        int updated = 0;
        int couldNotClassify = 0;
        const int batchSize = 100;

        var query = _db.ContentLogs.AsNoTracking()
            .Where(cl => cl.TikTokShopId == shop.Id && !string.IsNullOrEmpty(cl.VideoId));

        var totalCount = await query.CountAsync(cancellationToken);
        _logger.LogInformation("Total ContentLogs for shop {ShopCipher}: {Total}", shopCipher, totalCount);

        for (int skip = 0; skip < totalCount; skip += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await query
                .OrderBy(cl => cl.Id)
                .Skip(skip)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            foreach (var log in batch)
            {
                totalProcessed++;

                var latestMetric = await _db.ContentMetrics
                    .Where(m => m.ContentLogId == log.Id)
                    .OrderByDescending(m => m.CapturedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                var productsJson = latestMetric?.ProductsJson;

                bool hasIsLiveSignal = log.IsLive.HasValue;
                bool hasHasCommerceSignal = log.HasCommerce.HasValue;
                bool hasProductsSignal = !string.IsNullOrWhiteSpace(productsJson);

                if (!hasIsLiveSignal && !hasHasCommerceSignal && !hasProductsSignal)
                {
                    couldNotClassify++;
                    continue;
                }

                var classifiedTypeId = ContentTypeClassifier.ClassifyContentType(log.IsLive, log.HasCommerce, productsJson);

                bool shouldUpdate = false;
                if (!log.ContentTypeId.HasValue && classifiedTypeId.HasValue)
                {
                    shouldUpdate = true;
                }
                else if (log.ContentTypeId.HasValue && classifiedTypeId.HasValue && log.ContentTypeId != classifiedTypeId)
                {
                    shouldUpdate = true;
                }

                if (shouldUpdate && classifiedTypeId.HasValue)
                {
                    var tracked = await _db.ContentLogs.FindAsync(new object[] { log.Id }, cancellationToken: cancellationToken);
                    if (tracked != null)
                    {
                        tracked.ContentTypeId = classifiedTypeId;
                        tracked.UpdatedAt = DateTime.UtcNow;
                        updated++;
                    }
                }
            }

            if (updated > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation("Backfill progress for shop {ShopCipher}: {Processed}/{Total} processed, {Updated} updated", 
                shopCipher, Math.Min(skip + batchSize, totalCount), totalCount, updated);
        }

        _logger.LogInformation("Backfill completed for shop {ShopCipher}: {Total} processed, {Updated} updated, {CouldNotClassify} could not classify", 
            shopCipher, totalProcessed, updated, couldNotClassify);

        return (totalProcessed, updated, couldNotClassify);
    }

    /// <summary>
    /// Count how many ContentLogs dapat diklasifikasi vs tidak dapat diklasifikasi.
    /// Berguna untuk reporting dan diagnostik.
    /// </summary>
    public async Task<(int Classifiable, int NotClassifiable)> CountClassifiableAsync(CancellationToken cancellationToken = default)
    {
        var logs = await _db.ContentLogs.AsNoTracking()
            .Where(cl => !string.IsNullOrEmpty(cl.VideoId))
            .ToListAsync(cancellationToken);

        int classifiable = 0;
        int notClassifiable = 0;

        foreach (var log in logs)
        {
            var latestMetric = await _db.ContentMetrics
                .Where(m => m.ContentLogId == log.Id)
                .OrderByDescending(m => m.CapturedAt)
                .FirstOrDefaultAsync(cancellationToken);

            bool hasIsLiveSignal = log.IsLive.HasValue;
            bool hasHasCommerceSignal = log.HasCommerce.HasValue;
            bool hasProductsSignal = !string.IsNullOrWhiteSpace(latestMetric?.ProductsJson);

            if (hasIsLiveSignal || hasHasCommerceSignal || hasProductsSignal)
            {
                classifiable++;
            }
            else
            {
                notClassifiable++;
            }
        }

        return (classifiable, notClassifiable);
    }
}
