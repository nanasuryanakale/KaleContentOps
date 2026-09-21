using KaleContentOps.Data;
using KaleContentOps.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Services.DailySummary;

public sealed class DailySummaryService : IDailySummaryService
{
    private const string NonKkCode = "NON_KK";
    private const string KkCode = "KK";
    private const string AutoGmvCode = "AUTO_GMV_LIVE";

    private readonly AppDbContext _db;
    private readonly DailySummaryTargetOptions _targets;

    public DailySummaryService(AppDbContext db, DailySummaryTargetOptions targets)
    {
        _db = db;
        _targets = targets;
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

        var rows = days.Select(day =>
        {
            var values = grouped.Where(x => x.Date == day)
                .ToDictionary(x => x.ContentTypeId, x => new AggregatedValues(x.Count, x.TotalViews));
            var nonKk = GetValues(values, idByCode, NonKkCode);
            var kk = GetValues(values, idByCode, KkCode);
            var autoGmv = GetValues(values, idByCode, AutoGmvCode);

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
                NonKkContentStatus = GetStatus(nonKk.Count, _targets.NonKk.DailyContentTarget),
                NonKkViewsStatus = GetStatus(nonKk.Views, _targets.NonKk.DailyViewsTarget),
                KkContentStatus = GetStatus(kk.Count, _targets.KeranjangKuning.DailyContentTarget),
                KkViewsStatus = GetStatus(kk.Views, _targets.KeranjangKuning.DailyViewsTarget)
                ,
                // Total status is derived from Non-KK + Keranjang Kuning targets (Auto GMV has no target)
                TotalContentStatus = GetStatus(
                    nonKk.Count + kk.Count + (filter.IncludeAutoGmvInTotal ? autoGmv.Count : 0),
                    _targets.NonKk.DailyContentTarget + _targets.KeranjangKuning.DailyContentTarget),
                TotalViewsStatus = GetStatus(
                    nonKk.Views + kk.Views + (filter.IncludeAutoGmvInTotal ? autoGmv.Views : 0),
                    _targets.NonKk.DailyViewsTarget + _targets.KeranjangKuning.DailyViewsTarget)
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

        var targetAchievements = BuildTargetAchievements(summaries, days.Count);
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
                ShowAutoGmvColumns = filter.ShowAutoGmvColumns,
                // Expose total daily targets (sum of Non-KK and KK targets) for client-side status calculation
                TotalDailyContentTarget = _targets.NonKk.DailyContentTarget + _targets.KeranjangKuning.DailyContentTarget,
                TotalDailyViewsTarget = _targets.NonKk.DailyViewsTarget + _targets.KeranjangKuning.DailyViewsTarget
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
        int days)
    {
        var targets = new[]
        {
            (Code: NonKkCode, Target: _targets.NonKk),
            (Code: KkCode, Target: _targets.KeranjangKuning)
        };
        var achievements = new List<DailySummaryTargetAchievement>();
        foreach (var target in targets)
        {
            var summary = summaries.Single(x => x.Code == target.Code);
            var contentTarget = target.Target.DailyContentTarget * days;
            var viewsTarget = target.Target.DailyViewsTarget * days;
            achievements.Add(CreateAchievement(target.Code, summary.Name, "Jumlah Konten", contentTarget, summary.TotalCount));
            achievements.Add(CreateAchievement(target.Code, summary.Name, "Total Views", viewsTarget, summary.TotalViews));
        }

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
