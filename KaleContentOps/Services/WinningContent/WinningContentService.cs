using KaleContentOps.Data;
using KaleContentOps.Models;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Services.WinningContent;

/// <summary>
/// Winning Content backend service.
/// Phase 2A: data foundation (period dataset, latest metrics, temporary ER, baseline
/// window/average, median input, composition input). Backend only - no UI.
/// Phase 2B: multiplier and per-content-type leaderboard (Top-N by ER).
///
/// Query strategy: set-based, at most five round trips per BuildAsync regardless of
/// range length (content types, period content, period metrics, baseline content,
/// baseline metrics). No per-row queries, no Include chains, no full metric history
/// materialization. Latest metric per video is picked in memory from only the metrics
/// of the selected videos (same strategy as DailySummaryService), preserving NULLs -
/// no COALESCE fallback is invented.
///
/// AUTO_GMV_LIVE is never excluded (locked rule 2/13); archived rows are only excluded
/// when the caller explicitly opts out via WinningContentFilter.IncludeArchived=false
/// and even then never for AUTO_GMV_LIVE. ContentType is resolved by Code
/// (NON_KK / KK / AUTO_GMV_LIVE), never by hard-coded Id.
///
/// Leaderboard (locked rules 10-12): ranking ONLY. No minimum ER threshold, no minimum
/// multiplier threshold. The multiplier is information only and never filters entries.
/// </summary>
public sealed class WinningContentService : IWinningContentService
{
    /// <summary>Baseline window length in calendar days (locked rule 4/5).</summary>
    public const int BaselineDays = 30;

    /// <summary>Leaderboard size: NON_KK Top 4 (locked rule 11).</summary>
    public const int NonKkLeaderboardSize = 4;

    /// <summary>Leaderboard size: KK Top 4 (locked rule 11).</summary>
    public const int KkLeaderboardSize = 4;

    /// <summary>Leaderboard size: AUTO_GMV_LIVE Top 2 (locked rule 11).</summary>
    public const int AutoGmvLeaderboardSize = 2;

    private const string NonKkCode = "NON_KK";
    private const string KkCode = "KK";
    private const string AutoGmvCode = "AUTO_GMV_LIVE";

    private static readonly string[] WinningContentTypeCodes = { NonKkCode, KkCode, AutoGmvCode };

    private readonly AppDbContext _db;

    public WinningContentService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<WinningContentData> BuildAsync(WinningContentFilter filter, CancellationToken cancellationToken = default)
    {
        var start = filter.StartDate.Date;
        var end = filter.EndDate.Date;
        if (end < start)
        {
            (start, end) = (end, start);
        }
        var endExclusive = end.AddDays(1);
        var baselineStart = start.AddDays(-BaselineDays);

        var contentTypes = await _db.ContentTypes.AsNoTracking()
            .Where(x => x.Code == NonKkCode || x.Code == KkCode || x.Code == AutoGmvCode)
            .ToListAsync(cancellationToken);
        var typeById = contentTypes.ToDictionary(x => x.Id);
        // AUTO_GMV_LIVE is NEVER excluded by the archive flag (locked rule 2/13: it must
        // be included in ER, baseline, multiplier, leaderboard and composition).
        var autoGmvTypeId = contentTypes.FirstOrDefault(x => x.Code == AutoGmvCode)?.Id ?? -1;
        var archiveFilterApplies = new HashSet<int>(contentTypes.Select(x => x.Id).Except(new[] { autoGmvTypeId }));
        var typeIds = contentTypes.Select(x => x.Id).ToArray();

        // ---- 1) Baseline window [start - 30d, start): per-video ER, then AVG per type ----
        var baselineLogs = await _db.ContentLogs.AsNoTracking()
            .Where(x => x.VideoPostTime != null
                && x.VideoPostTime >= baselineStart
                && x.VideoPostTime < start
                && x.ContentTypeId != null
                && typeIds.Contains(x.ContentTypeId.Value)
                && (filter.IncludeArchived
                    || !archiveFilterApplies.Contains(x.ContentTypeId.Value)
                    || x.IsArchived != true))
            .Select(x => new { x.Id, x.ContentTypeId })
            .ToListAsync(cancellationToken);

        var baselineLogIds = baselineLogs.Select(x => x.Id).ToArray();
        var baselineMetrics = baselineLogIds.Length == 0
            ? new List<PeriodMetricRow>()
            : await _db.ContentMetrics.AsNoTracking()
                .Where(m => baselineLogIds.Contains(m.ContentLogId))
                .Select(m => new PeriodMetricRow
                {
                    ContentLogId = m.ContentLogId,
                    CapturedAt = m.CapturedAt,
                    MetricId = m.Id,
                    Views = m.Views,
                    Likes = m.Likes,
                    Comments = m.Comments,
                    Shares = m.Shares
                })
                .ToListAsync(cancellationToken);

        var latestBaselineMetricByLog = baselineMetrics
            .GroupBy(m => m.ContentLogId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => m.CapturedAt).ThenByDescending(m => m.MetricId).First());

        // Average of each video's OWN ER (locked rule 6) - never aggregate likes / views.
        // A baseline video is usable only with a valid ER (locked rule 7); excluded rows
        // do not count in N.
        var baselines = new List<WinningContentBaseline>();
        foreach (var type in contentTypes)
        {
            var baselineTypeLogIds = baselineLogs
                .Where(x => x.ContentTypeId == type.Id)
                .Select(x => x.Id)
                .ToArray();
            var perVideoErs = baselineTypeLogIds
                .Where(latestBaselineMetricByLog.ContainsKey)
                .Select(logId =>
                {
                    var m = latestBaselineMetricByLog[logId];
                    return WinningContentCalculator.ComputeEngagementRate(m.Likes, m.Comments, m.Shares, m.Views);
                })
                .Where(er => er.HasValue)
                .Select(er => er!.Value)
                .ToList();

            baselines.Add(new WinningContentBaseline
            {
                ContentTypeId = type.Id,
                ContentTypeCode = type.Code,
                BaselineStart = baselineStart,
                BaselineEndExclusive = start,
                VideoCount = perVideoErs.Count,
                AverageEngagementRate = perVideoErs.Count == 0
                    ? null
                    : perVideoErs.Average()
            });
        }

        var baselineByTypeId = baselines.ToDictionary(b => b.ContentTypeId, b => b.AverageEngagementRate);

        // ---- 2) Selected-period content (set-based; Auto GMV Live always included) ----
        var periodLogs = await _db.ContentLogs.AsNoTracking()
            .Where(x => x.VideoPostTime != null
                && x.VideoPostTime >= start
                && x.VideoPostTime < endExclusive
                && x.ContentTypeId != null
                && typeIds.Contains(x.ContentTypeId.Value)
                && (filter.IncludeArchived
                    || x.ContentTypeId == autoGmvTypeId
                    || x.IsArchived != true))
            .Select(x => new
            {
                x.Id,
                x.VideoId,
                x.Title,
                x.Username,
                x.VideoUrl,
                x.VideoPostTime,
                x.ContentTypeId
            })
            .ToListAsync(cancellationToken);

        // ---- 3) Metrics for exactly these videos; latest per video picked in memory ----
        var periodLogIds = periodLogs.Select(x => x.Id).ToArray();
        var periodMetrics = periodLogIds.Length == 0
            ? new List<PeriodMetricRow>()
            : await _db.ContentMetrics.AsNoTracking()
                .Where(m => periodLogIds.Contains(m.ContentLogId))
                .Select(m => new PeriodMetricRow
                {
                    ContentLogId = m.ContentLogId,
                    CapturedAt = m.CapturedAt,
                    MetricId = m.Id,
                    Views = m.Views,
                    Likes = m.Likes,
                    Comments = m.Comments,
                    Shares = m.Shares
                })
                .ToListAsync(cancellationToken);

        var latestPeriodMetricByLog = periodMetrics
            .GroupBy(m => m.ContentLogId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => m.CapturedAt).ThenByDescending(m => m.MetricId).First());

        var items = periodLogs
            .Select(log =>
            {
                latestPeriodMetricByLog.TryGetValue(log.Id, out var metric);
                var views = metric?.Views;
                var likes = metric?.Likes;
                var comments = metric?.Comments;
                var shares = metric?.Shares;
                var type = log.ContentTypeId != null ? typeById.GetValueOrDefault(log.ContentTypeId.Value) : null;
                var er = WinningContentCalculator.ComputeEngagementRate(likes, comments, shares, views);
                var baselineEr = type != null ? baselineByTypeId.GetValueOrDefault(type.Id) : null;
                return new WinningContentItem
                {
                    ContentLogId = log.Id,
                    VideoId = log.VideoId,
                    Title = log.Title,
                    Username = log.Username,
                    VideoUrl = log.VideoUrl,
                    VideoPostTime = log.VideoPostTime,
                    ContentTypeId = type?.Id,
                    ContentTypeCode = type?.Code,
                    ContentTypeName = type?.Name,
                    Views = views,
                    Likes = likes,
                    Comments = comments,
                    Shares = shares,
                    EngagementRate = er,
                    BaselineEngagementRate = baselineEr,
                    Multiplier = WinningContentCalculator.ComputeMultiplier(er, baselineEr),
                    LatestMetricCapturedAt = metric?.CapturedAt
                };
            })
            .OrderByDescending(x => x.VideoPostTime)
            .ThenByDescending(x => x.ContentLogId)
            .ToList();

        // ---- 4) Leaderboards: per content type, ER DESC then ContentLogId DESC, Top-N.
        // Ranking only (locked rules 10-12): no ER threshold, no multiplier threshold.
        var leaderboards = contentTypes
            .Select(type =>
            {
                var baseline = baselines.Single(b => b.ContentTypeId == type.Id);
                var take = LeaderboardTakeFor(type.Code);
                var entries = items
                    .Where(x => x.ContentTypeId == type.Id && x.EngagementRate.HasValue)
                    .OrderByDescending(x => x.EngagementRate)
                    .ThenByDescending(x => x.ContentLogId)
                    .Take(take)
                    .Select((x, index) => new WinningContentLeaderboardEntry
                    {
                        Rank = index + 1,
                        ContentLogId = x.ContentLogId,
                        VideoId = x.VideoId,
                        Title = x.Title,
                        Username = x.Username,
                        VideoUrl = x.VideoUrl,
                        VideoPostTime = x.VideoPostTime,
                        ContentTypeId = x.ContentTypeId,
                        ContentTypeCode = x.ContentTypeCode,
                        ContentTypeName = x.ContentTypeName,
                        Views = x.Views,
                        Likes = x.Likes,
                        Comments = x.Comments,
                        Shares = x.Shares,
                        EngagementRate = x.EngagementRate,
                        BaselineEngagementRate = x.BaselineEngagementRate,
                        Multiplier = x.Multiplier,
                        LatestMetricCapturedAt = x.LatestMetricCapturedAt
                    })
                    .ToList();

                return new WinningContentLeaderboard
                {
                    ContentTypeId = type.Id,
                    ContentTypeCode = type.Code,
                    BaselineEngagementRate = baseline.AverageEngagementRate,
                    BaselineVideoCount = baseline.VideoCount,
                    Entries = entries
                };
            })
            .ToList();

        // ---- 5) Median input: per-type Views of the selected period (no AVG approximation) ----
        var medianInputs = contentTypes.Select(type => new WinningContentMedianInput
        {
            ContentTypeId = type.Id,
            ContentTypeCode = type.Code,
            Views = items
                .Where(x => x.ContentTypeId == type.Id)
                .Select(x => x.Views)
                .ToList()
        }).ToList();

        // ---- 6) Composition input: content COUNTS per type (locked rule 8, not Views) ----
        var composition = contentTypes.Select(type => new WinningContentCompositionInput
        {
            ContentTypeId = type.Id,
            ContentTypeCode = type.Code,
            Count = items.Count(x => x.ContentTypeId == type.Id)
        }).ToList();

        // ---- 7) Median per content type (Phase 2C): exact median of valid Views.
        // NULL Views are excluded from the population (never treated as zero); a type
        // with no valid Views gets NULL median - never 0, never another type's median.
        var medians = new List<WinningContentMedian>();
        var medianByTypeId = new Dictionary<int, decimal?>();
        foreach (var type in contentTypes)
        {
            var typeViews = medianInputs.Single(mi => mi.ContentTypeId == type.Id).Views;
            var median = WinningContentCalculator.ComputeMedianViews(typeViews);
            medianByTypeId[type.Id] = median;
            medians.Add(new WinningContentMedian
            {
                ContentTypeId = type.Id,
                ContentTypeCode = type.Code,
                MedianViews = median,
                ValidViewsCount = typeViews.Count(v => v.HasValue),
                NullViewsCount = typeViews.Count(v => !v.HasValue)
            });
        }

        // ---- 8) Below Median per content type (Phase 2C): STRICT Views < category median.
        // Content with NULL Views can never satisfy the condition (locked rule 7/9).
        var belowMedian = contentTypes
            .Select(type =>
            {
                var median = medianByTypeId[type.Id];
                var entries = items
                    .Where(x => x.ContentTypeId == type.Id
                        && WinningContentCalculator.IsBelowMedian(x.Views, median))
                    .Select(x => new WinningContentBelowMedianEntry
                    {
                        ContentLogId = x.ContentLogId,
                        VideoId = x.VideoId,
                        Title = x.Title,
                        Username = x.Username,
                        VideoUrl = x.VideoUrl,
                        VideoPostTime = x.VideoPostTime,
                        ContentTypeId = x.ContentTypeId,
                        ContentTypeCode = x.ContentTypeCode,
                        ContentTypeName = x.ContentTypeName,
                        Views = x.Views,
                        MedianViews = median,
                        LatestMetricCapturedAt = x.LatestMetricCapturedAt
                    })
                    .OrderByDescending(x => x.Views)
                    .ThenByDescending(x => x.ContentLogId)
                    .ToList();

                return new WinningContentBelowMedianGroup
                {
                    ContentTypeId = type.Id,
                    ContentTypeCode = type.Code,
                    MedianViews = median,
                    Entries = entries
                }; 
            })
            .ToList();

        // ---- 9) Composition summary (Phase 2D): exact percentages over the three-category
        // total, computed centrally from the SAME counts as every other section (zero
        // additional queries). TotalCount = 0 -> NULL percentages + HasData=false.
        var compositionSummary = WinningContentCalculator.ComputeComposition(
            composition.Single(c => c.ContentTypeCode == NonKkCode).Count,
            composition.Single(c => c.ContentTypeCode == KkCode).Count,
            composition.Single(c => c.ContentTypeCode == AutoGmvCode).Count);

        return new WinningContentData
        {
            StartDate = start,
            EndDate = end,
            BaselineStart = baselineStart,
            BaselineEndExclusive = start,
            Items = items,
            Baselines = baselines,
            Leaderboards = leaderboards,
            MedianInputs = medianInputs,
            Medians = medians,
            BelowMedian = belowMedian,
            Composition = composition,
            CompositionSummary = compositionSummary
        };
    }

    private static int LeaderboardTakeFor(string code) => code switch
    {
        NonKkCode => NonKkLeaderboardSize,
        KkCode => KkLeaderboardSize,
        AutoGmvCode => AutoGmvLeaderboardSize,
        _ => 0
    };

    /// <summary>Minimal metric projection for set-based latest-metric selection.</summary>
    private sealed class PeriodMetricRow
    {
        public long ContentLogId { get; init; }
        public DateTime CapturedAt { get; init; }
        public long MetricId { get; init; }
        public long? Views { get; init; }
        public long? Likes { get; init; }
        public long? Comments { get; init; }
        public long? Shares { get; init; }
    }
}
