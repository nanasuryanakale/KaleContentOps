using KaleContentOps.Data;
using KaleContentOps.Services;
using KaleContentOps.Services.Targets;
using KaleContentOps.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Services.TeamPerformance;

/// <summary>
/// Team Performance business service (Phase 3 foundation).
///
/// Query strategy: set-based - at most three round trips for the workload data (period
/// content, period metrics, MasterPic list) plus a FIXED number of TargetService reads
/// (targetable types, two date resolutions, one daily series), all independent of the
/// range length and of the PIC count. Latest metric per log is picked in memory from only
/// the metrics of the selected logs. No per-row queries, no Include chains, no per-PIC
/// target lookups, no full metric-history materialization.
///
/// Locked business rules implemented here:
/// - Rule 1  : <c>IsArchived == true</c> content is ALWAYS counted (no archive filter).
/// - Rule 2/3/8: eligibility is NOT current <c>IsActive</c>; historical PICs stay
///             attributable. See <see cref="ApplyEmploymentPeriodFilter"/>.
/// - Rule 4  : the period is <c>VideoPostTime</c> only - never CreatedAt/UpdatedAt.
/// - Rule 5  : date-range conversion follows the existing application pattern
///             (shop-local calendar midnights compared directly against VideoPostTime,
///             exactly like DailySummaryService/TargetService). No SQL timezone functions.
/// - Rule 6  : the fair denominator is all ELIGIBLE PICs, including those with no content.
/// - Rule 7/10: <c>PicId == NULL</c> is team workload only - never an individual PIC row.
/// - Rule 9  : content with <c>VideoPostTime == NULL</c> never enters a date period.
///
/// Phase 4 (final business-rule sign-off) - target achievement:
/// - Target source   : the existing weekly <c>Targets</c> system via <see cref="ITargetService"/>
///                     (NON_KK + KK only; AUTO_GMV_LIVE has no target). SCD-2 resolution and the
///                     daily-series engine are NEVER re-implemented here - they are reused.
/// - Target Period   : Convention A = SUM(resolved daily weekly target) / 7 for the reporting
///                     period (missing-target days contribute zero; 1/7/30/90-day ranges work).
/// - Target Adil     : Team Target Period / EligiblePicCount - the SAME value for every eligible
///                     PIC (no proration). Never a fake zero: unconfigured target stays null.
/// - Actual (per PIC): uploads in the period assigned to that PIC, AUTO_GMV_LIVE EXCLUDED,
///                     archived INCLUDED (individual achievement metric).
/// - Team totals     : unchanged - AUTO_GMV_LIVE stays in workload/total, so the team total is
///                     deliberately NOT the sum of the individual achievement Actuals.
/// </summary>
public sealed class TeamPerformanceService : ITeamPerformanceService
{
    /// <summary>Content type code that never contributes to individual achievement Actual.</summary>
    private const string AutoGmvCode = "AUTO_GMV_LIVE";

    private readonly AppDbContext _db;
    private readonly IShopTimeZone _shopTimeZone;
    private readonly ITargetService _targetService;

    public TeamPerformanceService(AppDbContext db, IShopTimeZone shopTimeZone, ITargetService targetService)
    {
        _db = db;
        _shopTimeZone = shopTimeZone;
        _targetService = targetService;
    }

    public async Task<TeamPerformanceViewModel> BuildAsync(
        TeamPerformanceFilter filter,
        CancellationToken cancellationToken = default)
    {
        var start = filter.StartDate.Date;
        var end = filter.EndDate.Date;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        // Half-open window: [start, end + 1 day). Same convention as DailySummaryService and
        // TargetService: the period boundaries are shop-local calendar midnights and are
        // compared directly against VideoPostTime (the established application pattern - the
        // TikTok sync stores VideoPostTime as naive shop-local wall time; no UTC shift is
        // applied here, and no SQL timezone function is introduced). See ContentLogDateFilterTests.
        var endExclusive = end.AddDays(1);

        // ---- 1) Period content ----
        // VideoPostTime != null drops undated logs from any date period (rule 9).
        // IsArchived is deliberately NOT filtered (rule 1). No ContentType filter: the
        // locked Team Performance rules do not restrict content type, and Winning Content's
        // Top-N rules must NOT leak in here.
        var contentRows = await _db.ContentLogs.AsNoTracking()
            .Where(x => x.VideoPostTime != null
                && x.VideoPostTime >= start
                && x.VideoPostTime < endExclusive)
            .Select(x => new ContentRow
            {
                Id = x.Id,
                PicId = x.PicId,
                // Needed only to exclude AUTO_GMV_LIVE from the individual achievement Actual.
                // Everything else (team totals, ranking inputs) keeps every content type.
                ContentTypeCode = x.ContentType == null ? null : x.ContentType.Code
            })
            .ToListAsync(cancellationToken);

        // ---- 2) Latest metric per content log (established pattern) ----
        // Group by ContentLogId, order CapturedAt DESC then Id DESC, take the first row.
        var contentIds = contentRows.Select(x => x.Id).ToArray();
        var metricRows = contentIds.Length == 0
            ? new List<MetricRow>()
            : await _db.ContentMetrics.AsNoTracking()
                .Where(m => contentIds.Contains(m.ContentLogId))
                .Select(m => new MetricRow
                {
                    ContentLogId = m.ContentLogId,
                    CapturedAt = m.CapturedAt,
                    MetricId = m.Id,
                    Views = m.Views
                })
                .ToListAsync(cancellationToken);

        // Views NULL contributes 0 to a SUM - the same established convention as
        // DailySummaryService / TargetService (`?? 0L`). A missing measurement is NEVER
        // converted into an arbitrary non-zero value that would alter ranking (Step 7).
        var latestViewsByLog = metricRows
            .GroupBy(m => m.ContentLogId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => m.CapturedAt).ThenByDescending(m => m.MetricId).First().Views ?? 0L);

        long ViewsOf(long contentLogId) => latestViewsByLog.GetValueOrDefault(contentLogId, 0L);

        var totalContentCount = contentRows.Count;
        var totalViews = contentRows.Sum(x => ViewsOf(x.Id));

        // ---- 3) "Belum Diisi" (PicId NULL): team workload only, never a PIC row ----
        var unassignedContentCount = contentRows.Count(x => x.PicId == null);
        var unassignedViews = contentRows.Where(x => x.PicId == null).Sum(x => ViewsOf(x.Id));
        var assignedContentCount = totalContentCount - unassignedContentCount;
        var assignedViews = totalViews - unassignedViews;

        // ---- 4) PIC eligibility / fair denominator ----
        // JoinDate / ResignDate are projected (Phase 3A) so eligibility is decided from the
        // real employment period - never from IsActive, CreatedAt or UpdatedAt.
        var pics = await _db.MasterPics.AsNoTracking()
            .Select(p => new PicInfo
            {
                Id = p.Id,
                Name = p.Name,
                IsActive = p.IsActive,
                JoinDate = p.JoinDate,
                ResignDate = p.ResignDate
            })
            .ToListAsync(cancellationToken);
        var eligiblePics = ApplyEmploymentPeriodFilter(pics, start, end);

        // ---- 4b) Team target period (Phase 4) - REUSED from TargetService, never re-derived ----
        var periodStart = DateOnly.FromDateTime(start);
        var periodEnd = DateOnly.FromDateTime(end);

        // Targetable content types (NON_KK / KK) come from TargetService's own read model;
        // AUTO_GMV_LIVE never appears there because SaveTargetAsync rejects it.
        var targetable = await _targetService.GetCurrentTargetsAsync(cancellationToken);
        var nonKkTypeId = targetable.FirstOrDefault(x =>
            string.Equals(x.ContentTypeCode, TargetService.NonKkCode, StringComparison.OrdinalIgnoreCase))?.ContentTypeId;
        var kkTypeId = targetable.FirstOrDefault(x =>
            string.Equals(x.ContentTypeCode, TargetService.KkCode, StringComparison.OrdinalIgnoreCase))?.ContentTypeId;

        // "Configured for the applicable period" = a target version covers the period END date.
        // TargetService keeps coverage gapless from the first version onward (SaveTargetAsync
        // trims neighbours), so coverage on EndDate <=> some coverage inside the period.
        // Date-based resolution via the sanctioned API - never latest-row-wins, never an
        // in-memory re-implementation of EffectiveFrom/EffectiveTo.
        var nonKkVersion = nonKkTypeId is int nonKkId
            ? await _targetService.GetTargetForDateAsync(nonKkId, periodEnd, cancellationToken)
            : null;
        var kkVersion = kkTypeId is int kkId
            ? await _targetService.GetTargetForDateAsync(kkId, periodEnd, cancellationToken)
            : null;
        var targetConfigured = nonKkVersion != null || kkVersion != null;

        // Convention A (locked): Team Target Period = SUM(resolved daily weekly target) / 7.
        // Missing-target days contribute zero (same engine Daily Summary uses).
        var targetSeries = await _targetService.GetDailyTargetSeriesAsync(periodStart, periodEnd, cancellationToken);
        var teamTargetPeriod = targetSeries.NonKk.UploadPeriodTarget + targetSeries.Kk.UploadPeriodTarget;

        // Target Adil (locked): Team Target Period / eligible PIC count, identical for every
        // eligible PIC, no proration. Null when the target is not configured (no fake zero)
        // and when there is no eligible PIC (no rows => no division at all).
        var targetAdil = targetConfigured && eligiblePics.Count > 0
            ? teamTargetPeriod / eligiblePics.Count
            : (decimal?)null;

        // ---- 5) Per-PIC aggregation ----
        var contentByPicId = contentRows
            .Where(x => x.PicId != null)
            .GroupBy(x => x.PicId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var aggregates = eligiblePics
            .Select(pic =>
            {
                contentByPicId.TryGetValue(pic.Id, out var picContent);
                picContent ??= new List<ContentRow>();
                var views = picContent.Sum(x => ViewsOf(x.Id));
                // Individual achievement Actual: everything EXCEPT AUTO_GMV_LIVE (archived stays).
                var achievementActual = picContent.Count(x =>
                    !string.Equals(x.ContentTypeCode, AutoGmvCode, StringComparison.OrdinalIgnoreCase));
                return new PicAggregate(pic, picContent.Count, achievementActual, views);
            })
            .ToList();

        // ---- 6) Ranking: PICs with content by TotalViews DESC, ContentCount DESC, name ASC.
        //           Zero-content eligible PICs stay in the denominator list but carry no rank.
        var ranked = aggregates
            .Where(a => a.ContentCount > 0)
            .OrderByDescending(a => a.TotalViews)
            .ThenByDescending(a => a.ContentCount)
            .ThenBy(a => a.Pic.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var withoutContent = aggregates
            .Where(a => a.ContentCount == 0)
            .OrderBy(a => a.Pic.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<TeamPerformancePicRow>(aggregates.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            rows.Add(BuildRow(ranked[i], i + 1, totalContentCount, totalViews, targetAdil, targetConfigured));
        }
        foreach (var aggregate in withoutContent)
        {
            rows.Add(BuildRow(aggregate, null, totalContentCount, totalViews, targetAdil, targetConfigured));
        }

        return new TeamPerformanceViewModel
        {
            StartDate = start,
            EndDate = end,
            TimeZoneId = _shopTimeZone.TimeZoneId,
            TotalContentCount = totalContentCount,
            TotalViews = totalViews,
            AssignedContentCount = assignedContentCount,
            AssignedViews = assignedViews,
            UnassignedContentCount = unassignedContentCount,
            UnassignedViews = unassignedViews,
            EligiblePicCount = eligiblePics.Count,
            PicsWithContentCount = ranked.Count,
            TeamTargetPeriod = teamTargetPeriod,
            TargetConfigured = targetConfigured,
            EmploymentPeriodDataAvailable = EmploymentPeriodDataAvailable,
            Pics = rows
        };
    }

    private static TeamPerformancePicRow BuildRow(
        PicAggregate aggregate,
        int? rank,
        int totalContentCount,
        long totalViews,
        decimal? targetAdil,
        bool targetConfigured) => new()
        {
            Rank = rank,
            PicId = aggregate.Pic.Id,
            PicName = aggregate.Pic.Name,
            IsActive = aggregate.Pic.IsActive,
            ContentCount = aggregate.ContentCount,
            // Achievement Actual (AUTO_GMV_LIVE excluded) - the value Selisih/Status compare.
            Actual = aggregate.AchievementActual,
            TargetAdil = targetAdil,
            TargetConfigured = targetConfigured && targetAdil.HasValue,
            TotalViews = aggregate.TotalViews,
            AverageViewsPerContent = aggregate.ContentCount == 0
                ? null
                : (decimal)aggregate.TotalViews / aggregate.ContentCount,
            ContentSharePercent = totalContentCount == 0
                ? null
                : Math.Round(aggregate.ContentCount * 100m / totalContentCount, 2),
            ViewsSharePercent = totalViews == 0
                ? null
                : Math.Round(aggregate.TotalViews * 100m / totalViews, 2)
        };

    // =====================================================================
    // EMPLOYMENT-PERIOD ELIGIBILITY - Phase 3A (LOCKED RULE)
    // ---------------------------------------------------------------------
    // A PIC is eligible for the reporting period [startDate, endDate] (both inclusive
    // business dates) when the employment period overlaps it:
    //     JoinDate <= EndDate
    //     AND (ResignDate IS NULL OR ResignDate >= StartDate)
    // where JoinDate is the first member day and ResignDate the last member day.
    //
    // DELIBERATELY NOT USED as the eligibility signal:
    //   - IsActive: CURRENT status only; an inactive PIC resigned 2026-06-30 must still be
    //     eligible for a May-2026 report (locked rules 2/3/8),
    //   - CreatedAt / UpdatedAt / today: record maintenance, never employment dates,
    //     and using them as a ResignDate stand-in is explicitly forbidden.
    //
    // This method is the SINGLE place the overlap rule lives. The MasterPics table is
    // tiny (single-digit rows) and is already fetched in one round trip with the rest of
    // the build (Step 10 review: no N+1, no extra query), so the rule is applied in
    // memory here rather than duplicated as an EF predicate that could drift.
    // =====================================================================
    private static IReadOnlyList<PicInfo> ApplyEmploymentPeriodFilter(
        IReadOnlyList<PicInfo> pics,
        DateTime startDate,
        DateTime endDate)
    {
        var start = DateOnly.FromDateTime(startDate);
        var end = DateOnly.FromDateTime(endDate);

        return pics
            .Where(p => p.JoinDate <= end
                && (p.ResignDate is null || p.ResignDate.Value >= start))
            .ToList();
    }

    /// <summary>
    /// Phase 3A: JoinDate/ResignDate now exist (Phase 2A schema, applied) and are consumed
    /// by <see cref="ApplyEmploymentPeriodFilter"/>, so employment-period data IS available
    /// and the provisional "denominator not final" UI notice no longer applies.
    /// </summary>
    private const bool EmploymentPeriodDataAvailable = true;

    private sealed class ContentRow
    {
        public long Id { get; init; }
        public int? PicId { get; init; }

        /// <summary>Content type CODE (null when unclassified) - only used to drop AUTO_GMV_LIVE.</summary>
        public string? ContentTypeCode { get; init; }
    }

    private sealed class MetricRow
    {
        public long ContentLogId { get; init; }
        public DateTime CapturedAt { get; init; }
        public long MetricId { get; init; }
        public long? Views { get; init; }
    }

    private sealed class PicInfo
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }

        /// <summary>First business day the PIC joined (required, Phase 2A).</summary>
        public DateOnly JoinDate { get; init; }

        /// <summary>Last business day the PIC was a member; NULL = no known end date.</summary>
        public DateOnly? ResignDate { get; init; }
    }

    private readonly record struct PicAggregate(PicInfo Pic, int ContentCount, int AchievementActual, long TotalViews);
}
