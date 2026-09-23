using KaleContentOps.Data;
using KaleContentOps.Models;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Services.Targets;

/// <summary>
/// Domain service for Menu Targets persistence (Phase 2).
/// Business rules:
/// - Only NON_KK and KK may have targets; validation uses ContentType.Code (never hardcoded ids).
/// - Same-day edit updates the version whose EffectiveFrom equals that day (no new row).
/// - New-day edit closes the previous active version (EffectiveTo = day before the new EffectiveFrom)
///   and creates a new active version; close + create are committed in ONE SaveChangesAsync,
///   i.e. a single atomic transaction on the relational provider (SQL Server).
/// - AUTO_GMV_LIVE (or any other code) is rejected; unknown content type ids are rejected.
/// </summary>
public class TargetService : ITargetService
{
    public const string NonKkCode = "NON_KK";
    public const string KkCode = "KK";

    private readonly AppDbContext _db;

    public TargetService(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TargetCurrentItem>> GetCurrentTargetsAsync(CancellationToken cancellationToken = default)
    {
        // Fetch all targetable content types (NON_KK, KK)
        var contentTypes = await _db.ContentTypes.AsNoTracking()
            .Where(c => c.Code == NonKkCode || c.Code == KkCode)
            .ToListAsync(cancellationToken);

        // Fetch all active (current) target versions for these content types
        var activeTargets = await _db.Targets.AsNoTracking()
            .Where(t => t.EffectiveTo == null
                && (t.ContentType!.Code == NonKkCode || t.ContentType!.Code == KkCode))
            .OrderByDescending(t => t.EffectiveFrom)
            .ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);

        // Group active targets by ContentTypeId to handle potential data corruption (multiple active rows)
        var targetsByContentTypeId = activeTargets
            .GroupBy(t => t.ContentTypeId)
            .ToDictionary(g => g.Key, g => g.First()); // Newest wins deterministically

        // Build result: one row per targetable content type (NON_KK first, then KK)
        // If no target record exists, use null/empty values so UI can show empty inputs
        var result = contentTypes
            .OrderByDescending(ct => string.Equals(ct.Code, NonKkCode, StringComparison.OrdinalIgnoreCase))
            .Select(ct => 
            {
                var target = targetsByContentTypeId.TryGetValue(ct.Id, out var t) ? t : null;
                return new TargetCurrentItem
                {
                    ContentTypeId = ct.Id,
                    ContentTypeCode = ct.Code,
                    ContentTypeName = ct.Name,
                    TargetUpload = target?.TargetUpload ?? 0,
                    TargetViews = target?.TargetViews ?? 0L,
                    EffectiveFrom = target?.EffectiveFrom ?? default,
                    EffectiveTo = target?.EffectiveTo
                };
            })
            .ToList();

        return result;
    }

    /// <inheritdoc />
    public async Task<Target?> GetTargetForDateAsync(int contentTypeId, DateOnly reportDate, CancellationToken cancellationToken = default)
    {
        // Corrupted overlapping history must not be resolved randomly: newest EffectiveFrom
        // (then highest Id) wins deterministically - same tiebreak style as the latest-metric
        // query in DailySummaryService (OrderByDescending CapturedAt, ThenByDescending Id).
        return await _db.Targets.AsNoTracking()
            .Where(t => t.ContentTypeId == contentTypeId
                && t.EffectiveFrom <= reportDate
                && (t.EffectiveTo == null || t.EffectiveTo >= reportDate))
            .OrderByDescending(t => t.EffectiveFrom)
            .ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TargetSaveResult> SaveTargetAsync(TargetSaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TargetUpload < 0)
        {
            return TargetSaveResult.Fail(
                TargetSaveErrorCodes.TargetUploadNegative,
                "Target Upload tidak boleh negatif.");
        }

        if (request.TargetViews < 0)
        {
            return TargetSaveResult.Fail(
                TargetSaveErrorCodes.TargetViewsNegative,
                "Target Views tidak boleh negatif.");
        }

        var contentType = await _db.ContentTypes.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.ContentTypeId, cancellationToken);
        if (contentType is null)
        {
            return TargetSaveResult.Fail(
                TargetSaveErrorCodes.ContentTypeNotFound,
                $"Content type dengan id {request.ContentTypeId} tidak ditemukan.");
        }

        // Validation by Code, never by integer id.
        if (!string.Equals(contentType.Code, NonKkCode, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contentType.Code, KkCode, StringComparison.OrdinalIgnoreCase))
        {
            return TargetSaveResult.Fail(
                TargetSaveErrorCodes.ContentTypeNotTargetable,
                $"Content type '{contentType.Code}' tidak boleh memiliki target. Hanya NON_KK dan KK yang diizinkan.");
        }

        var active = await _db.Targets
            .Where(t => t.ContentTypeId == request.ContentTypeId && t.EffectiveTo == null)
            .OrderByDescending(t => t.EffectiveFrom)
            .ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // Scenario: initial target - no active version yet.
        if (active is null)
        {
            var created = new Target
            {
                ContentTypeId = request.ContentTypeId,
                TargetUpload = request.TargetUpload,
                TargetViews = request.TargetViews,
                EffectiveFrom = request.Today,
                EffectiveTo = null
            };
            _db.Targets.Add(created);
            await SaveAsync(cancellationToken);
            return TargetSaveResult.Ok(created, contentType.Code, createdNewVersion: true);
        }

        // Defensive: an active version starting in the future is a data anomaly; saving would
        // create a second active row and violate the unique filtered index. Fail loudly instead.
        if (active.EffectiveFrom > request.Today)
        {
            return TargetSaveResult.Fail(
                TargetSaveErrorCodes.ActiveVersionAnomaly,
                $"Versi target aktif dimulai {active.EffectiveFrom:yyyy-MM-dd}, setelah tanggal hari ini ({request.Today:yyyy-MM-dd}). Perbaiki data sebelum menyimpan.");
        }

        // Scenario A: same-day edit - update in place, never create a new version.
        if (active.EffectiveFrom == request.Today)
        {
            active.TargetUpload = request.TargetUpload;
            active.TargetViews = request.TargetViews;
            active.UpdatedAt = DateTime.UtcNow;
            await SaveAsync(cancellationToken);
            return TargetSaveResult.Ok(active, contentType.Code, createdNewVersion: false);
        }

        // Scenario B: new-day version - close old + create new, committed in one batch so the
        // close is rolled back automatically if the insert fails (single implicit transaction).
        active.EffectiveTo = request.Today.AddDays(-1);
        active.UpdatedAt = DateTime.UtcNow;

        var newVersion = new Target
        {
            ContentTypeId = request.ContentTypeId,
            TargetUpload = request.TargetUpload,
            TargetViews = request.TargetViews,
            EffectiveFrom = request.Today,
            EffectiveTo = null
        };
        _db.Targets.Add(newVersion);
        await SaveAsync(cancellationToken);
        return TargetSaveResult.Ok(newVersion, contentType.Code, createdNewVersion: true);
    }

    /// <inheritdoc />
    public async Task<TargetActualItem?> GetActualAsync(int contentTypeId, DateOnly endDate, int days = 7, CancellationToken cancellationToken = default)
    {
        // Fetch ContentType first to get code + name
        var contentType = await _db.ContentTypes.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contentTypeId, cancellationToken);
        if (contentType is null)
            return null;

        // Date range for rolling window: [endDate - days + 1, endDate]
        var startDate = endDate.AddDays(-(days - 1));
        var startDateTime = new DateTime(startDate.Year, startDate.Month, startDate.Day, 0, 0, 0, DateTimeKind.Utc);
        var endDateTime = new DateTime(endDate.Year, endDate.Month, endDate.Day, 23, 59, 59, DateTimeKind.Utc);

        // Fetch all ContentLogs in range for this content type (exclude archived).
        var contentLogs = await _db.ContentLogs.AsNoTracking()
            .Where(cl => cl.ContentTypeId == contentTypeId
                && cl.VideoPostTime >= startDateTime
                && cl.VideoPostTime <= endDateTime
                && cl.IsArchived != true)
            .Select(cl => new { cl.Id })
            .ToListAsync(cancellationToken);

        if (contentLogs.Count == 0)
        {
            // No content in range, return zero actuals
            return new TargetActualItem
            {
                ContentTypeId = contentTypeId,
                ContentTypeCode = contentType.Code,
                ContentTypeName = contentType.Name,
                ActualUpload = 0,
                ActualViews = 0
            };
        }

        var contentLogIds = contentLogs.Select(cl => cl.Id).ToArray();

        // Fetch latest metric per content log (matching pattern from DailySummaryService)
        var latestMetrics = await _db.ContentMetrics.AsNoTracking()
            .Where(m => contentLogIds.Contains(m.ContentLogId))
            .GroupBy(m => m.ContentLogId)
            .Select(g => new
            {
                ContentLogId = g.Key,
                Views = g
                    .OrderByDescending(m => m.CapturedAt)
                    .ThenByDescending(m => m.Id)
                    .Select(m => m.Views)
                    .FirstOrDefault() ?? 0L
            })
            .ToListAsync(cancellationToken);

        var totalViews = latestMetrics.Sum(m => m.Views);

        return new TargetActualItem
        {
            ContentTypeId = contentTypeId,
            ContentTypeCode = contentType.Code,
            ContentTypeName = contentType.Name,
            ActualUpload = contentLogs.Count,
            ActualViews = totalViews
        };
    }

    /// <summary>
    /// Persistence seam: every save path goes through exactly one SaveChangesAsync call
    /// (one implicit transaction on SQL Server). Virtual for testing (rollback simulation);
    /// the class is therefore not sealed, unlike some other services.
    /// </summary>
    protected virtual Task<int> SaveAsync(CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);
}

/// <summary>Stable validation error codes returned by <see cref="ITargetService.SaveTargetAsync"/>.</summary>
public static class TargetSaveErrorCodes
{
    public const string TargetUploadNegative = "TARGET_UPLOAD_NEGATIVE";
    public const string TargetViewsNegative = "TARGET_VIEWS_NEGATIVE";
    public const string ContentTypeNotFound = "CONTENT_TYPE_NOT_FOUND";
    public const string ContentTypeNotTargetable = "CONTENT_TYPE_NOT_TARGETABLE";
    public const string ActiveVersionAnomaly = "ACTIVE_VERSION_ANOMALY";
}
