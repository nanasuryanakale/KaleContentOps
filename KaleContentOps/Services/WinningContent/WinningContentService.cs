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
                    Shares = m.Shares,
                    Reach = m.Reach,
                    FullWatchRate = m.FullWatchRate,
                    DemographicsJson = m.DemographicsJson
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
                x.ContentTypeId,
                x.ProductionMethodId,
                // Feature 2/3: production method via the existing FK (LEFT JOIN in the
                // same query - no extra round trip). NULL for Auto GMV Live by locked
                // mapping; display convention "null -> Self Produce" matches Content Log.
                ProductionMethodCode = x.ProductionMethod != null ? x.ProductionMethod.Code : null,
                ProductionMethodName = x.ProductionMethod != null ? x.ProductionMethod.Name : null
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
                    Shares = m.Shares,
                    Reach = m.Reach,
                    FullWatchRate = m.FullWatchRate,
                    DemographicsJson = m.DemographicsJson
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
                var sharesParsed = ParseDemographicShares(metric?.DemographicsJson);
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
                    LatestMetricCapturedAt = metric?.CapturedAt,
                    // Phase B additions - pure transports of existing columns:
                    Reach = metric?.Reach,
                    FullWatchRate = metric?.FullWatchRate,
                    ProductionMethodId = log.ProductionMethodId ?? null,
                    ProductionMethodCode = log.ProductionMethodCode,
                    ProductionMethodName = log.ProductionMethodName,
                    EngagementCount = likes.HasValue && comments.HasValue && shares.HasValue
                        ? likes.Value + comments.Value + shares.Value
                        : null,
                    MaleShare = sharesParsed.MaleShare,
                    FemaleShare = sharesParsed.FemaleShare,
                    Age18_34Share = sharesParsed.Age18_34Share
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
                        FullWatchRate = x.FullWatchRate,
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

        // ---- 10) Feature 4 funnel: honest per-video averages over the selected period.
        var (avgViews, avgReach, avgEngagement) = WinningContentCalculator.ComputeFunnelAverages(items
            .Select(x => (x.Views, x.Reach, x.Likes, x.Comments, x.Shares)));
        var funnel = new WinningContentFunnel
        {
            AverageViewsPerVideo = avgViews,
            AverageReachPerVideo = avgReach,
            AverageEngagementPerVideo = avgEngagement,
            ViewsPercent = avgViews > 0 ? 100m : avgViews == 0 ? null : null,
            ReachPercent = WinningContentCalculator.ComputeFunnelPercent(avgReach, avgViews),
            EngagementPercent = WinningContentCalculator.ComputeFunnelPercent(avgEngagement, avgViews),
            VideosWithViews = items.Count(x => x.Views.HasValue),
            VideosWithReach = items.Count(x => x.Reach.HasValue),
            VideosWithEngagement = items.Count(x => x.Likes.HasValue && x.Comments.HasValue && x.Shares.HasValue)
        };

        // ---- 11) Feature 2/3 production-method breakdown (NON_KK + KK only; median rule untouched).
        var productionBreakdown = new WinningContentProductionBreakdown
        {
            TotalKkNonKkCount = items.Count(x => x.ContentTypeCode != AutoGmvCode),
            Methods = BuildProductionBreakdown(items, belowMedian)
        };

        // ---- 12) Feature 5 audience snapshot over the EXISTING baseline window
        // [start - 30d, start): reuses latestBaselineMetricByLog (already fetched for
        // the ER baseline) - no second date-window implementation, no extra queries.
        var baselineDemographics = latestBaselineMetricByLog
            .Select(kv => (ContentLogId: kv.Key,
                           Views: (long?)kv.Value.Views,
                           DemographicsJson: kv.Value.DemographicsJson))
            .ToList();
        var audience = BuildAudienceSnapshot(start, items, baselineDemographics);

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
            CompositionSummary = compositionSummary,
            Funnel = funnel,
            ProductionBreakdown = productionBreakdown,
            Audience = audience
        };
    }

    /// <summary>
    /// Feature 2/3: production-method rows over NON_KK + KK period content. Auto GMV
    /// Live is excluded (locked mapping: it carries no production method). NULL
    /// ProductionMethodId displays as SELF_PRODUCE - the same convention Content Log
    /// uses. Median rule untouched: a video is "below median" exactly when the
    /// backend put it in Model.BelowMedian (strict Views &lt; its own type's median).
    /// </summary>
    private static List<WinningContentProductionMethodStats> BuildProductionBreakdown(
        IReadOnlyList<WinningContentItem> items,
        IReadOnlyList<WinningContentBelowMedianGroup> belowMedian)
    {
        var belowSet = belowMedian
            .SelectMany(g => g.Entries)
            .Select(e => e.ContentLogId)
            .ToHashSet();

        var labelByCode = new Dictionary<string, string>
        {
            [AiProduceCode] = "AI Produce",
            [SelfProduceCode] = "Self Produce"
        };

        var rows = new List<WinningContentProductionMethodStats>();
        foreach (var code in new[] { AiProduceCode, SelfProduceCode })
        {
            // Display mapping: NULL method -> Self Produce (Content Log convention).
            var methodItems = items
                .Where(x => x.ProductionMethodCode == code
                    || (code == SelfProduceCode && x.ProductionMethodCode == null))
                // Auto GMV Live never carries a production method, but guard anyway.
                .Where(x => x.ContentTypeCode != AutoGmvCode)
                .ToList();

            var ers = methodItems
                .Where(x => x.EngagementRate.HasValue)
                .Select(x => x.EngagementRate!.Value)
                .ToList();
            var views = methodItems
                .Where(x => x.Views.HasValue)
                .Select(x => x.Views!.Value)
            .ToList();

            var total = methodItems.Count;
            var below = methodItems.Count(x => belowSet.Contains(x.ContentLogId));

            rows.Add(new WinningContentProductionMethodStats
            {
                Code = code,
                Label = labelByCode[code],
                TotalCount = total,
                BelowMedianCount = below,
                BelowMedianPercentage = total == 0 ? null : below * 100m / total,
                // decimal math (List<long>.Average() would return double):
                AverageViewsPerVideo = views.Count == 0 ? null : views.Sum() / (decimal)views.Count,
                AverageEngagementRate = ers.Count == 0 ? null : ers.Average() // List<decimal>.Average() is decimal
            });
        }

        return rows;
    }

    /// <summary>
    /// Demographics parser for the Feature 5 snapshot. Values in DemographicsJson are
    /// viewer shares on the 0..1 scale (verified: TikTokVideoService stores the raw
    /// viewer_profile percentages; ContentLogListItem documents "0..1 decimals" and
    /// ContentLogDemographicsMappingTests pins male:0.4 -> 0.4m). Output is 0..100.
    /// Corrupt JSON, missing keys and absent metrics all stay NULL - never fabricated.
    /// </summary>
    public static (decimal? MaleShare, decimal? FemaleShare, decimal? Age18_34Share) ParseDemographicShares(string? demographicsJson)
    {
        if (string.IsNullOrWhiteSpace(demographicsJson))
        {
            return (null, null, null);
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(demographicsJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return (null, null, null);
            }

            decimal? Read(decimal? v) => v;

            decimal? ReadProp(string key)
            {
                if (!root.TryGetProperty(key, out var prop)) return null;
                if (prop.ValueKind == System.Text.Json.JsonValueKind.Number && prop.TryGetDecimal(out var d)) return d;
                if (prop.ValueKind == System.Text.Json.JsonValueKind.String
                    && decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
                return null;
            }

            var male = ReadProp("male");
            var female = ReadProp("female");

            decimal? age18_34 = null;
            if (root.TryGetProperty("ages", out var ages) && ages.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                decimal? ReadAge(string key)
                {
                    if (!ages.TryGetProperty(key, out var prop)) return null;
                    if (prop.ValueKind == System.Text.Json.JsonValueKind.Number && prop.TryGetDecimal(out var d)) return d;
                    if (prop.ValueKind == System.Text.Json.JsonValueKind.String
                        && decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
                    return null;
                }

                var a = ReadAge("18-24");
                var b = ReadAge("25-34");
                age18_34 = a.HasValue || b.HasValue ? (a ?? 0m) + (b ?? 0m) : null;
            }

            return (Read(male), Read(female), age18_34);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Feature 5 audience snapshot over the EXISTING baseline window
    /// [start - 30d, start) - the same window the ER baseline uses (no second
    /// date-window implementation). Shares are 0..1 per video; aggregation is the
    /// Views-weighted average (documented on WinningContentAudienceSnapshot).
    /// Follower % has NO verified source (only the NewFollowers COUNT is ingested) and
    /// is rendered as an explicit unavailable state - never fabricated.
    /// </summary>
    private static WinningContentAudienceSnapshot BuildAudienceSnapshot(
        DateTime start,
        IReadOnlyList<WinningContentItem> items,
        IReadOnlyList<(long ContentLogId, long? Views, string? DemographicsJson)> baselineDemographics)
    {
        var currentMale = WinningContentCalculator.ComputeWeightedSharePercent(items.Select(x => (x.Views, x.MaleShare)));
        var currentFemale = WinningContentCalculator.ComputeWeightedSharePercent(items.Select(x => (x.Views, x.FemaleShare)));
        var currentAge = WinningContentCalculator.ComputeWeightedSharePercent(items.Select(x => (x.Views, x.Age18_34Share)));

        var previousMale = WinningContentCalculator.ComputeWeightedSharePercent(baselineDemographics.Select(x => (x.Views, ParseDemographicShares(x.DemographicsJson).MaleShare)));
        var previousFemale = WinningContentCalculator.ComputeWeightedSharePercent(baselineDemographics.Select(x => (x.Views, ParseDemographicShares(x.DemographicsJson).FemaleShare)));
        var previousAge = WinningContentCalculator.ComputeWeightedSharePercent(baselineDemographics.Select(x => (x.Views, ParseDemographicShares(x.DemographicsJson).Age18_34Share)));

        WinningContentAudienceMetric Metric(string key, string label, decimal? current, decimal? previous, bool available = true, string? reason = null) => new()
        {
            Key = key,
            Label = label,
            CurrentPercentage = current,
            PreviousPercentage = previous,
            DeltaPoints = WinningContentCalculator.ComputeDeltaPoints(current, previous),
            IsAvailable = available,
            UnavailableReason = reason
        };

        var follower = Metric("follower", "Follower", null, null,
            available: false,
            reason: "Data persentase follower belum tersedia di sistem (hanya jumlah NewFollowers yang tersinkron).");

        return new WinningContentAudienceSnapshot
        {
            PreviousStart = start.AddDays(-BaselineDays),
            PreviousEndExclusive = start,
            // Contributor counts: a video counts only when it actually participates in a
            // weighted share (positive Views AND at least one demographic share present).
            CurrentVideosWithDemographics = items.Count(x => x.Views > 0
                && (x.MaleShare.HasValue || x.FemaleShare.HasValue || x.Age18_34Share.HasValue)),
            PreviousVideosWithDemographics = baselineDemographics.Count(x => x.Views > 0
                && (ParseDemographicShares(x.DemographicsJson).MaleShare.HasValue
                    || ParseDemographicShares(x.DemographicsJson).FemaleShare.HasValue
                    || ParseDemographicShares(x.DemographicsJson).Age18_34Share.HasValue)),
            Metrics = new[]
            {
                follower,
                Metric("male", "Male", currentMale, previousMale),
                Metric("female", "Female", currentFemale, previousFemale),
                Metric("age18_34", "Usia 18-34", currentAge, previousAge)
            }
        };
    }

    private static int LeaderboardTakeFor(string code) => code switch
    {
        NonKkCode => NonKkLeaderboardSize,
        KkCode => KkLeaderboardSize,
        AutoGmvCode => AutoGmvLeaderboardSize,
        _ => 0
    };

    // Feature 2/3: locked production-method catalog (no new methods are invented).
    // Labels fall back to these when the ProductionMethods table has no matching row.
    private const string AiProduceCode = "AI_PRODUCE";
    private const string SelfProduceCode = "SELF_PRODUCE";

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
        // Feature 4/5 transports + %Full Watch badge display (existing columns - no new ingestion):
        public long? Reach { get; init; }
        public decimal? FullWatchRate { get; init; }
        public string? DemographicsJson { get; init; }
    }
}
