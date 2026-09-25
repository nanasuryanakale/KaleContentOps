using KaleContentOps.Data;
using KaleContentOps.Services.Targets;
using KaleContentOps.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Services.DailySummary;

public sealed class DailySummaryService : IDailySummaryService
{
    private const string NonKkCode = "NON_KK";
    private const string KkCode = "KK";
    private const string AutoGmvCode = "AUTO_GMV_LIVE";

    private readonly AppDbContext _db;
    // Phase 6: the Menu Targets table (via ITargetService) is the single source of truth
    // for targets - no appsettings/daily-summary target config, no duplicate versioning.
    private readonly ITargetService _targetService;

    public DailySummaryService(AppDbContext db, ITargetService targetService)
    {
        _db = db;
        _targetService = targetService;
    }

    public async Task<DailySummaryPageModel> BuildAsync(DailySummaryFilter filter, CancellationToken cancellationToken = default)
    {
        var start = filter.StartDate.Date;
        var end = filter.EndDate.Date;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var endExclusive = end.AddDays(1);
        var contentTypes = await _db.ContentTypes.AsNoTracking()
            .Where(x => x.Code == NonKkCode || x.Code == KkCode || x.Code == AutoGmvCode)
            .ToListAsync(cancellationToken);

        var typeIds = contentTypes.Select(x => x.Id).ToArray();
        var contentRows = await _db.ContentLogs.AsNoTracking()
            .Where(x => x.VideoPostTime != null
                && x.VideoPostTime >= start
                && x.VideoPostTime < endExclusive
                && typeIds.Contains(x.ContentTypeId!.Value)
                && (filter.IncludeArchived || x.IsArchived != true))
            .Select(x => new ContentRow
            {
                Id = x.Id,
                Date = x.VideoPostTime!.Value.Date,
                ContentTypeId = x.ContentTypeId!.Value
            })
            .ToListAsync(cancellationToken);

        var contentIds = contentRows.Select(x => x.Id).ToArray();
        var metricRows = contentIds.Length == 0
            ? new List<MetricRow>()
            : await _db.ContentMetrics.AsNoTracking()
                .Where(x => contentIds.Contains(x.ContentLogId))
                .Select(x => new MetricRow
                {
                    Id = x.Id,
                    ContentLogId = x.ContentLogId,
                    CapturedAt = x.CapturedAt,
                    Views = x.Views
                })
                .ToListAsync(cancellationToken);

        var latestViewsByContentId = metricRows
            .GroupBy(x => x.ContentLogId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(x => x.CapturedAt)
                    .ThenByDescending(x => x.Id)
                    .First()
                    .Views ?? 0L);

        var grouped = contentRows
            .Select(log => new
            {
                Date = log.Date,
                ContentTypeId = log.ContentTypeId,
                Views = latestViewsByContentId.GetValueOrDefault(log.Id, 0L)
            })
            .GroupBy(x => new { x.Date, x.ContentTypeId })
            .Select(g => new
            {
                g.Key.Date,
                g.Key.ContentTypeId,
                Count = g.Count(),
                TotalViews = g.Sum(x => x.Views)
            })
            .ToList();

        var typeByCode = contentTypes.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        var idByCode = contentTypes.ToDictionary(x => x.Code, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var days = Enumerable.Range(0, (end - start).Days + 1)
            .Select(offset => start.AddDays(offset))
            .ToList();

        // Phase 6: per-day targets resolved by the TargetService versioning rule
        // (EffectiveFrom <= date <= EffectiveTo; never the current/latest version for
        // historical dates; future versions never match before their EffectiveFrom).
        // Weekly targets are derived to daily via WeeklyTarget / 7 (docs/daily-summary-spec.md
        // sections 10 and 12). One bulk call for the whole range - no N+1.
        var targetSeries = await _targetService.GetDailyTargetSeriesAsync(
            DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), cancellationToken);

        var rows = days.Select((day, dayIndex) =>
        {
            var values = grouped.Where(x => x.Date == day)
                .ToDictionary(x => x.ContentTypeId, x => new AggregatedValues(x.Count, x.TotalViews));
            var nonKk = GetValues(values, idByCode, NonKkCode);
            var kk = GetValues(values, idByCode, KkCode);
            var autoGmv = GetValues(values, idByCode, AutoGmvCode);
            // Phase 6 UAT fix: per-content-type daily targets - each type's upload and
            // views statuses compare against ITS OWN target version for this date.
            var nonKkUploadTarget = targetSeries.NonKk.UploadByDayIndex(dayIndex);
            var nonKkViewsTarget = targetSeries.NonKk.ViewsByDayIndex(dayIndex);
            var kkUploadTarget = targetSeries.Kk.UploadByDayIndex(dayIndex);
            var kkViewsTarget = targetSeries.Kk.ViewsByDayIndex(dayIndex);

            return new DailySummaryRowViewModel
            {
                Date = day,
                NonKkCount = nonKk.Count,
                NonKkViews = nonKk.Views,
                KkCount = kk.Count,
                KkViews = kk.Views,
                AutoGmvCount = autoGmv.Count,
                AutoGmvViews = autoGmv.Views,
                TotalCount = nonKk.Count + kk.Count + (filter.IncludeAutoGmvInTotal ? autoGmv.Count : 0),
                TotalViews = nonKk.Views + kk.Views + (filter.IncludeAutoGmvInTotal ? autoGmv.Views : 0),
                NonKkContentStatus = GetStatus(nonKk.Count, nonKkUploadTarget),
                NonKkViewsStatus = GetStatus(nonKk.Views, nonKkViewsTarget),
                KkContentStatus = GetStatus(kk.Count, kkUploadTarget),
                KkViewsStatus = GetStatus(kk.Views, kkViewsTarget),
                // TOTAL status: actual includes Auto GMV per the existing toggle, but the target
                // stays NON-KK + KK (Auto GMV has no target - spec sections 16/17).
                TotalContentStatus = GetStatus(
                    nonKk.Count + kk.Count + (filter.IncludeAutoGmvInTotal ? autoGmv.Count : 0),
                    nonKkUploadTarget + kkUploadTarget),
                TotalViewsStatus = GetStatus(
                    nonKk.Views + kk.Views + (filter.IncludeAutoGmvInTotal ? autoGmv.Views : 0),
                    nonKkViewsTarget + kkViewsTarget),
                // Per-row daily targets: the client-side Include Auto GMV toggle re-evaluates the
                // TOTAL status without a reload and must see the same targets the server used.
                TotalContentTarget = nonKkUploadTarget + kkUploadTarget,
                TotalViewsTarget = nonKkViewsTarget + kkViewsTarget
            };
        }).ToList();

        var summaries = new List<DailySummaryContentTypeSummary>();
        foreach (var code in new[] { NonKkCode, KkCode, AutoGmvCode })
        {
            var typeId = idByCode.GetValueOrDefault(code);
            var totalCount = grouped.Where(x => x.ContentTypeId == typeId).Sum(x => x.Count);
            var totalViews = grouped.Where(x => x.ContentTypeId == typeId).Sum(x => x.TotalViews);
            var contentType = typeByCode.GetValueOrDefault(code);
            summaries.Add(new DailySummaryContentTypeSummary
            {
                Code = code,
                Name = contentType?.Name ?? GetDisplayName(code),
                TotalCount = totalCount,
                TotalViews = totalViews,
                AvgContentPerDay = Math.Round((decimal)totalCount / days.Count, 2),
                AvgViewsPerDay = Math.Round((decimal)totalViews / days.Count, 2)
            });
        }

        var totalContent = summaries.Sum(x => x.TotalCount);
        var composition = summaries.Select(summary => new DailySummaryComposition
        {
            Code = summary.Code,
            Name = summary.Name,
            Count = summary.TotalCount,
            Percentage = totalContent == 0 ? 0 : Math.Round((decimal)summary.TotalCount / totalContent * 100, 2)
        }).ToList();

        // Pencapaian vs Target: per content type, the period target is the exact sum of
        // that type's per-date weekly values / 7. With a single target version this equals
        // DailyTarget * days (spec section 12); across an Effective Date change each day
        // keeps its own version. Upload and Views come from the same per-type versions.
        var targetAchievements = BuildTargetAchievements(
            summaries,
            targetSeries.NonKk,
            targetSeries.Kk);
        return new DailySummaryPageModel
        {
            Data = new DailySummaryViewModel
            {
                StartDate = start,
                EndDate = end,
                Rows = rows,
                ContentTypes = contentTypes,
                IncludeArchived = filter.IncludeArchived,
                IncludeAutoGmvInTotal = filter.IncludeAutoGmvInTotal,
                ShowNonKkColumns = filter.ShowNonKkColumns,
                ShowKkColumns = filter.ShowKkColumns,
                ShowAutoGmvColumns = filter.ShowAutoGmvColumns
            },
            TypeSummaries = summaries,
            Composition = composition,
            TargetAchievements = targetAchievements,
            ShowNonKk = filter.ShowNonKkColumns,
            ShowKk = filter.ShowKkColumns,
            ShowAutoGmv = filter.ShowAutoGmvColumns
        };
    }

    private List<DailySummaryTargetAchievement> BuildTargetAchievements(
        IReadOnlyCollection<DailySummaryContentTypeSummary> summaries,
        DailyTargetSeries.PerTypeSeries nonKkTargets,
        DailyTargetSeries.PerTypeSeries kkTargets)
    {
        var achievements = new List<DailySummaryTargetAchievement>();

        var nonKkSummary = summaries.Single(x => x.Code == NonKkCode);
        achievements.Add(CreateAchievement(NonKkCode, nonKkSummary.Name, "Jumlah Konten", nonKkTargets.UploadPeriodTarget, nonKkSummary.TotalCount));
        achievements.Add(CreateAchievement(NonKkCode, nonKkSummary.Name, "Total Views", nonKkTargets.ViewsPeriodTarget, nonKkSummary.TotalViews));

        var kkSummary = summaries.Single(x => x.Code == KkCode);
        achievements.Add(CreateAchievement(KkCode, kkSummary.Name, "Jumlah Konten", kkTargets.UploadPeriodTarget, kkSummary.TotalCount));
        achievements.Add(CreateAchievement(KkCode, kkSummary.Name, "Total Views", kkTargets.ViewsPeriodTarget, kkSummary.TotalViews));

        return achievements;
    }

    private static DailySummaryTargetAchievement CreateAchievement(
        string code, string name, string metric, decimal target, decimal actual)
    {
        var variance = actual - target;
        return new DailySummaryTargetAchievement
        {
            Code = code,
            ContentTypeName = name,
            MetricName = metric,
            TargetPeriodValue = target,
            ActualValue = actual,
            VarianceValue = variance,
            Status = actual >= target ? "achieved" : "not achieved"
        };
    }

    private static (int Count, long Views) GetValues(
        IReadOnlyDictionary<int, AggregatedValues> values,
        IReadOnlyDictionary<string, int> ids,
        string code)
    {
        if (!ids.TryGetValue(code, out var id) || !values.TryGetValue(id, out var value))
        {
            return (0, 0);
        }

        return (value.Count, value.TotalViews);
    }

    private sealed class ContentRow
    {
        public long Id { get; set; }
        public DateTime Date { get; set; }
        public int ContentTypeId { get; set; }
    }

    private sealed class MetricRow
    {
        public long Id { get; set; }
        public long ContentLogId { get; set; }
        public DateTime CapturedAt { get; set; }
        public long? Views { get; set; }
    }

    private readonly record struct AggregatedValues(int Count, long TotalViews);

    private static string GetStatus(decimal actual, decimal target) => actual >= target ? "above" : "below";

    private static string GetDisplayName(string code) => code switch
    {
        NonKkCode => "Non-KK",
        KkCode => "Keranjang Kuning",
        AutoGmvCode => "Auto GMV Live",
        _ => code
    };
}
