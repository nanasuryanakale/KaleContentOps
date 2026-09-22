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
    public async Task<IReadOnlyDictionary<string, Target>> GetCurrentTargetsAsync(CancellationToken cancellationToken = default)
    {
        // Newest-first so that, in the unlikely case of corrupted data (multiple active rows),
        // the latest version wins deterministically instead of depending on row order.
        var rows = await _db.Targets.AsNoTracking()
            .Where(t => t.EffectiveTo == null
                && (t.ContentType!.Code == NonKkCode || t.ContentType!.Code == KkCode))
            .OrderByDescending(t => t.EffectiveFrom)
            .ThenByDescending(t => t.Id)
            .Select(t => new { Target = t, Code = t.ContentType!.Code })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            result[row.Code] = row.Target;
        }

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
            return TargetSaveResult.Ok(created, createdNewVersion: true);
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
            return TargetSaveResult.Ok(active, createdNewVersion: false);
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
        return TargetSaveResult.Ok(newVersion, createdNewVersion: true);
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
