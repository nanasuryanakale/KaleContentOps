using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Services.Targets;

/// <summary>
/// Domain service for Menu Targets persistence (Phase 2 + Phase 4b).
/// Business rules:
/// - Only NON_KK and KK may have targets; validation uses ContentType.Code (never hardcoded ids).
/// - Versions are keyed by (ContentTypeId, EffectiveFrom) - a save whose Effective Date equals an
///   existing version REVISES that version in place (no duplicate effective dates, deterministic lookup).
/// - A save with a new date inserts a version and trims the neighbours' coverage windows
///   (previous.EffectiveTo = date - 1; new.EffectiveTo = next.EffectiveFrom - 1). This single rule
///   covers backdating (historical period keeps the older version) and future scheduling
///   (the version covering today stays active until the scheduled date arrives).
/// - Close/trim + create are committed in ONE SaveChangesAsync, i.e. a single atomic transaction
///   on the relational provider (SQL Server).
/// - Current/scheduled/history reads resolve by DATE, never by "latest row wins".
/// - AUTO_GMV_LIVE (or any other code) is rejected; unknown content type ids are rejected.
/// </summary>
public class TargetService : ITargetService
{
    public const string NonKkCode = "NON_KK";
    public const string KkCode = "KK";

    private readonly AppDbContext _db;
    private readonly IShopTimeZone _shopTimeZone;

    public TargetService(AppDbContext db, IShopTimeZone shopTimeZone)
    {
        _db = db;
        _shopTimeZone = shopTimeZone;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TargetCurrentItem>> GetCurrentTargetsAsync(CancellationToken cancellationToken = default)
    {
        var today = _shopTimeZone.Today();

        var contentTypes = await _db.ContentTypes.AsNoTracking()
            .Where(c => c.Code == NonKkCode || c.Code == KkCode)
            .ToListAsync(cancellationToken);

        var allVersions = await _db.Targets.AsNoTracking()
            .Where(t => (t.ContentType!.Code == NonKkCode || t.ContentType!.Code == KkCode)
                && t.EffectiveFrom <= today
                && (t.EffectiveTo == null || t.EffectiveTo >= today))
            .OrderByDescending(t => t.EffectiveFrom)
            .ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);

        // Group by content type: corrupted overlapping history must not be resolved randomly -
        // newest EffectiveFrom (then highest Id) wins deterministically.
        var currentByContentTypeId = allVersions
            .GroupBy(t => t.ContentTypeId)
            .ToDictionary(g => g.Key, g => g.First());

        var changedByNames = await ResolveUserNamesAsync(
            currentByContentTypeId.Values.Select(t => t.ChangedByUserId), cancellationToken);

        // One row per targetable content type (NON_KK first, then KK). Missing target -> zeros placeholder.
        var result = contentTypes
            .OrderByDescending(ct => string.Equals(ct.Code, NonKkCode, StringComparison.OrdinalIgnoreCase))
            .Select(ct =>
            {
                currentByContentTypeId.TryGetValue(ct.Id, out var target);
                return new TargetCurrentItem
                {
                    ContentTypeId = ct.Id,
                    ContentTypeCode = ct.Code,
                    ContentTypeName = ct.Name,
                    TargetUpload = target?.TargetUpload ?? 0,
                    TargetViews = target?.TargetViews ?? 0L,
                    EffectiveFrom = target?.EffectiveFrom ?? default,
                    EffectiveTo = target?.EffectiveTo,
                    ChangedByUserId = target?.ChangedByUserId,
                    ChangedByName = target?.ChangedByUserId is null ? null : changedByNames.GetValueOrDefault(target.ChangedByUserId),
                    ChangedAt = target?.UpdatedAt
                };
            })
            .ToList();

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TargetCurrentItem>> GetScheduledTargetsAsync(CancellationToken cancellationToken = default)
    {
        var today = _shopTimeZone.Today();

        var contentTypes = await _db.ContentTypes.AsNoTracking()
            .Where(c => c.Code == NonKkCode || c.Code == KkCode)
            .ToListAsync(cancellationToken);

        var scheduled = await _db.Targets.AsNoTracking()
            .Where(t => (t.ContentType!.Code == NonKkCode || t.ContentType!.Code == KkCode)
                && t.EffectiveFrom > today)
            .OrderBy(t => t.EffectiveFrom)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var changedByNames = await ResolveUserNamesAsync(scheduled.Select(t => t.ChangedByUserId), cancellationToken);

        // NON_KK first, then KK; within a content type by Effective Date ascending.
        var orderById = contentTypes
            .OrderByDescending(ct => string.Equals(ct.Code, NonKkCode, StringComparison.OrdinalIgnoreCase))
            .Select((ct, index) => (ct.Id, index))
            .ToDictionary(x => x.Id, x => x.index);

        return scheduled
            .OrderBy(t => orderById.GetValueOrDefault(t.ContentTypeId, int.MaxValue))
            .ThenBy(t => t.EffectiveFrom)
            .Select(t => new TargetCurrentItem
            {
                ContentTypeId = t.ContentTypeId,
                ContentTypeCode = contentTypes.FirstOrDefault(c => c.Id == t.ContentTypeId)?.Code ?? string.Empty,
                ContentTypeName = contentTypes.FirstOrDefault(c => c.Id == t.ContentTypeId)?.Name ?? string.Empty,
                TargetUpload = t.TargetUpload,
                TargetViews = t.TargetViews,
                EffectiveFrom = t.EffectiveFrom,
                EffectiveTo = t.EffectiveTo,
                ChangedByUserId = t.ChangedByUserId,
                ChangedByName = t.ChangedByUserId is null ? null : changedByNames.GetValueOrDefault(t.ChangedByUserId),
                ChangedAt = t.UpdatedAt
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<Target?> GetTargetForDateAsync(int contentTypeId, DateOnly reportDate, CancellationToken cancellationToken = default)
    {
        // Corrupted overlapping history must not be resolved randomly: newest EffectiveFrom
        // (then highest Id) wins deterministically - same tiebreak style as the latest-metric
        // query in DailySummaryService (OrderByDescending CapturedAt, ThenByDescending Id).
        // Scheduled future versions never match dates before their EffectiveFrom; historical
        // periods resolve to the version that was effective on that date (Phase 5 readiness).
        return await _db.Targets.AsNoTracking()
            .Where(t => t.ContentTypeId == contentTypeId
                && t.EffectiveFrom <= reportDate
                && (t.EffectiveTo == null || t.EffectiveTo >= reportDate))
            .OrderByDescending(t => t.EffectiveFrom)
            .ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TargetHistoryItem>> GetTargetHistoryAsync(int contentTypeId, int maxRows = 50, CancellationToken cancellationToken = default)
    {
        var today = _shopTimeZone.Today();

        var contentType = await _db.ContentTypes.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contentTypeId, cancellationToken);
        if (contentType is null)
        {
            return Array.Empty<TargetHistoryItem>();
        }

        var versions = await _db.Targets.AsNoTracking()
            .Where(t => t.ContentTypeId == contentTypeId)
            .OrderByDescending(t => t.EffectiveFrom)
            .ThenByDescending(t => t.Id)
            .Take(Math.Clamp(maxRows, 1, 500))
            .ToListAsync(cancellationToken);

        var changedByNames = await ResolveUserNamesAsync(versions.Select(t => t.ChangedByUserId), cancellationToken);

        return versions
            .Select(t => new TargetHistoryItem
            {
                ContentTypeId = t.ContentTypeId,
                ContentTypeCode = contentType.Code,
                EffectiveDate = t.EffectiveFrom,
                EffectiveUntil = t.EffectiveTo,
                TargetUpload = t.TargetUpload,
                TargetViews = t.TargetViews,
                ChangedByUserId = t.ChangedByUserId,
                ChangedByName = t.ChangedByUserId is null ? null : changedByNames.GetValueOrDefault(t.ChangedByUserId),
                ChangedAt = t.UpdatedAt,
                IsScheduled = t.EffectiveFrom > today,
                IsCurrent = t.EffectiveFrom <= today && (t.EffectiveTo == null || t.EffectiveTo >= today)
            })
            .ToList();
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

        // "Berlaku Mulai": user-provided effective date; absent -> server date (legacy auto-save).
        var effectiveDate = request.EffectiveDate ?? request.Today;

        // Load the full version chain once (tracked - trims and inserts share one SaveChangesAsync).
        var versions = await _db.Targets
            .Where(t => t.ContentTypeId == request.ContentTypeId)
            .OrderBy(t => t.EffectiveFrom)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;

        // --------------------------------------------------------------
        // Case A: no versions at all -> create the first one.
        // --------------------------------------------------------------
        if (versions.Count == 0)
        {
            var created = new Target
            {
                ContentTypeId = request.ContentTypeId,
                TargetUpload = request.TargetUpload,
                TargetViews = request.TargetViews,
                EffectiveFrom = effectiveDate,
                EffectiveTo = null,
                ChangedByUserId = request.ChangedByUserId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.Targets.Add(created);
            await SaveAsync(cancellationToken);
            return TargetSaveResult.Ok(created, contentType.Code, createdNewVersion: true, isScheduled: effectiveDate > request.Today);
        }

        // --------------------------------------------------------------
        // Case B: a version with exactly this Effective Date exists -> REVISE it in place.
        // Never a duplicate effective date (unique index backs this up); values + audit are updated.
        // --------------------------------------------------------------
        var sameDate = versions.LastOrDefault(v => v.EffectiveFrom == effectiveDate);
        if (sameDate is not null)
        {
            sameDate.TargetUpload = request.TargetUpload;
            sameDate.TargetViews = request.TargetViews;
            sameDate.UpdatedAt = now;
            sameDate.ChangedByUserId = request.ChangedByUserId;
            await SaveAsync(cancellationToken);
            return TargetSaveResult.Ok(sameDate, contentType.Code, createdNewVersion: false, isScheduled: effectiveDate > request.Today);
        }

        // --------------------------------------------------------------
        // Case C: new Effective Date -> insert + trim neighbours (one atomic save).
        // previous.EffectiveTo = effectiveDate - 1 and new.EffectiveTo = next.EffectiveFrom - 1
        // keep coverage windows non-overlapping and gapless between surviving versions,
        // whether the new date is backdated, today, or in the future (scheduled).
        // --------------------------------------------------------------
        var previous = versions.LastOrDefault(v => v.EffectiveFrom < effectiveDate);
        var next = versions.FirstOrDefault(v => v.EffectiveFrom > effectiveDate);

        // Trim the previous version's coverage. Its VALUES are untouched: ChangedByUserId/UpdatedAt
        // keep describing the last content change of that version (audit is per-version, not per-trim).
        if (previous is not null)
        {
            previous.EffectiveTo = effectiveDate.AddDays(-1);
        }

        var newVersion = new Target
        {
            ContentTypeId = request.ContentTypeId,
            TargetUpload = request.TargetUpload,
            TargetViews = request.TargetViews,
            EffectiveFrom = effectiveDate,
            // Scheduled chain: a later version keeps the null window; this one ends where the next begins.
            EffectiveTo = next is null ? null : next.EffectiveFrom.AddDays(-1),
            ChangedByUserId = request.ChangedByUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Targets.Add(newVersion);
        await SaveAsync(cancellationToken);
        return TargetSaveResult.Ok(newVersion, contentType.Code, createdNewVersion: true, isScheduled: effectiveDate > request.Today);
    }

    /// <inheritdoc />
    public async Task<TargetActualItem?> GetActualAsync(int contentTypeId, DateOnly endDate, int days = 7, CancellationToken cancellationToken = default)
    {
        // Fetch ContentType first to get code + name
        var contentType = await _db.ContentTypes.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contentTypeId, cancellationToken);
        if (contentType is null)
            return null;

        // Phase 5 boundary fix: half-open window [start, endExclusive) exactly as DailySummaryService
        // filters VideoPostTime, so the last day keeps its full 24h (the previous 23:59:59
        // end-of-day cut dropped late-day content). VideoPostTime is stored as naive shop-local
        // wall time by the TikTok sync, so shop-local dates compare directly (no UTC shift).
        var (startDate, endExclusive) = RollingWindow(endDate, days);
        var startDateTime = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusiveDateTime = endExclusive.ToDateTime(TimeOnly.MinValue);

        var (uploadCounts, totalViews) = await AggregateActualsAsync(
            new[] { contentTypeId }, startDateTime, endExclusiveDateTime, cancellationToken);

        return new TargetActualItem
        {
            ContentTypeId = contentTypeId,
            ContentTypeCode = contentType.Code,
            ContentTypeName = contentType.Name,
            ActualUpload = uploadCounts.GetValueOrDefault(contentTypeId),
            ActualViews = totalViews.GetValueOrDefault(contentTypeId)
        };
    }

    /// <inheritdoc />
    public async Task<TargetActualsSummary> GetActualsSummaryAsync(DateOnly endDate, int days = 7, CancellationToken cancellationToken = default)
    {
        // Same targetable set + ordering as GetCurrentTargetsAsync (NON_KK first, then KK);
        // AUTO_GMV_LIVE and other codes never appear on the Menu Targets page.
        var contentTypes = await _db.ContentTypes.AsNoTracking()
            .Where(c => c.Code == NonKkCode || c.Code == KkCode)
            .ToListAsync(cancellationToken);
        var orderedTypes = contentTypes
            .OrderByDescending(ct => string.Equals(ct.Code, NonKkCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var typeIds = orderedTypes.Select(ct => ct.Id).ToArray();

        // Target effective on the period END date: date-based resolution via the existing
        // versioning rule (EffectiveFrom <= endDate <= EffectiveTo), never "latest row wins";
        // scheduled future versions never match before their EffectiveFrom.
        var targets = await _db.Targets.AsNoTracking()
            .Where(t => typeIds.Contains(t.ContentTypeId)
                && t.EffectiveFrom <= endDate
                && (t.EffectiveTo == null || t.EffectiveTo >= endDate))
            .OrderByDescending(t => t.EffectiveFrom)
            .ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);
        var targetByTypeId = targets
            .GroupBy(t => t.ContentTypeId)
            .ToDictionary(g => g.Key, g => g.First());

        // Rolling window: exactly `days` calendar days, [endDate - days + 1, endDate] inclusive.
        var (startDate, endExclusive) = RollingWindow(endDate, days);
        var startDateTime = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusiveDateTime = endExclusive.ToDateTime(TimeOnly.MinValue);

        var (uploadCounts, totalViews) = await AggregateActualsAsync(
            typeIds, startDateTime, endExclusiveDateTime, cancellationToken);

        var items = orderedTypes
            .Select(ct =>
            {
                targetByTypeId.TryGetValue(ct.Id, out var target);
                return new TargetActualItem
                {
                    ContentTypeId = ct.Id,
                    ContentTypeCode = ct.Code,
                    ContentTypeName = ct.Name,
                    ActualUpload = uploadCounts.GetValueOrDefault(ct.Id),
                    ActualViews = totalViews.GetValueOrDefault(ct.Id),
                    TargetUpload = target?.TargetUpload ?? 0,
                    TargetViews = target?.TargetViews ?? 0L,
                    TargetEffectiveFrom = target?.EffectiveFrom
                };
            })
            .ToList();

        return new TargetActualsSummary
        {
            StartDate = startDate,
            EndDate = endDate,
            Days = days,
            TimeZoneId = _shopTimeZone.TimeZoneId,
            Items = items
        };
    }

    /// <summary>
    /// Rolling calendar window: [endDate - days + 1, endDate], exactly `days` calendar days
    /// (never 7x24h, never Monday-Sunday, never 8 dates). Returns the inclusive start and the
    /// exclusive end (start of the day after endDate) for half-open filtering.
    /// </summary>
    private static (DateOnly Start, DateOnly EndExclusive) RollingWindow(DateOnly endDate, int days)
    {
        var clamped = Math.Max(1, days);
        return (endDate.AddDays(-(clamped - 1)), endDate.AddDays(1));
    }

    /// <summary>
    /// Set-based actual aggregation shared by GetActualAsync and GetActualsSummaryAsync
    /// (same conventions as DailySummaryService):
    /// - uploads = COUNT of ContentLogs with a classified ContentTypeId in the window
    ///   (unclassified ContentLogs never leak into a bucket); archived excluded (IsArchived != true);
    /// - views = SUM over logs of the LATEST metric (CapturedAt DESC, Id DESC tiebreak);
    ///   logs without metrics contribute 0 and never fail or duplicate the query.
    /// Two bulk queries total - never one lookup per ContentLog (no N+1).
    /// Returns per-type dictionaries keyed by ContentTypeId.
    /// </summary>
    private async Task<(Dictionary<int, int> UploadCounts, Dictionary<int, long> TotalViews)> AggregateActualsAsync(
        IReadOnlyCollection<int> contentTypeIds, DateTime startInclusive, DateTime endExclusive, CancellationToken cancellationToken)
    {
        var contentRows = await _db.ContentLogs.AsNoTracking()
            .Where(cl => cl.VideoPostTime != null
                && cl.VideoPostTime >= startInclusive
                && cl.VideoPostTime < endExclusive
                && cl.ContentTypeId != null
                && contentTypeIds.Contains(cl.ContentTypeId.Value)
                && cl.IsArchived != true)
            .Select(cl => new { cl.Id, cl.ContentTypeId })
            .ToListAsync(cancellationToken);

        var contentIds = contentRows.Select(x => x.Id).ToArray();
        var metricRows = contentIds.Length == 0
            ? new List<MetricSnapshot>()
            : await _db.ContentMetrics.AsNoTracking()
                .Where(m => contentIds.Contains(m.ContentLogId))
                .Select(m => new MetricSnapshot { ContentLogId = m.ContentLogId, CapturedAt = m.CapturedAt, Id = m.Id, Views = m.Views })
                .ToListAsync(cancellationToken);

        var latestViewsByContentId = metricRows
            .GroupBy(m => m.ContentLogId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => m.CapturedAt).ThenByDescending(m => m.Id).First().Views ?? 0L);

        var uploadCounts = contentRows
            .GroupBy(x => x.ContentTypeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var totalViews = contentRows
            .GroupBy(x => x.ContentTypeId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => latestViewsByContentId.GetValueOrDefault(x.Id, 0L)));

        return (uploadCounts, totalViews);
    }

    /// <summary>Client-side projection row for the latest-metric selection (mirrors DailySummaryService.MetricRow).</summary>
    private sealed class MetricSnapshot
    {
        public long ContentLogId { get; set; }
        public DateTime CapturedAt { get; set; }
        public long Id { get; set; }
        public long? Views { get; set; }
    }

    /// <summary>
    /// Resolves stable Identity user ids to display names for audit columns
    /// (DisplayName falling back to UserName), server-side only.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveUserNamesAsync(
        IEnumerable<string?> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var users = await _db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName, u.DisplayName })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(
            u => u.Id,
            u => string.IsNullOrWhiteSpace(u.DisplayName) ? (u.UserName ?? u.Id) : u.DisplayName);
    }

    /// <summary>
    /// Persistence seam: every save path goes through exactly one SaveChangesAsync call
    /// (one implicit transaction on SQL Server). Virtual for testing (rollback simulation);
    /// the class is therefore not sealed, unlike some other services.
    /// </summary>
    protected virtual Task<int> SaveAsync(CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);
}
