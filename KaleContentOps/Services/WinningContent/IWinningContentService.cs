namespace KaleContentOps.Services.WinningContent;

/// <summary>
/// Winning Content Phase 2A data foundation (backend only - no UI).
///
/// Locked business rules implemented here:
/// - Content types: NON_KK, KK, AUTO_GMV_LIVE (AUTO_GMV_LIVE always included).
/// - Content date = ContentLog.VideoPostTime, inclusive range [StartDate, EndDate].
/// - Temporary ER (while Saves is unavailable):
///     ER = (Likes + Comments + Shares) / Views
///   with Views = 0 (and missing Likes/Comments/Shares) yielding NULL ER - never a
///   divide-by-zero and never a fabricated value. When Saves exists, extend
///   WinningContentCalculator.ComputeEngagementRate to add COALESCE(Saves, 0) to the
///   numerator only; call sites and consumers stay unchanged.
/// - Baseline: the 30 calendar days immediately before SelectedPeriodStart, per content
///   type, as the AVERAGE OF EACH VIDEO'S OWN ER (not aggregate likes/views).
/// </summary>
public interface IWinningContentService
{
    /// <summary>
    /// Builds the complete Phase 2A dataset for the selected period:
    /// per-video rows with the latest metric + temporary ER, the per-content-type
    /// baseline over the preceding 30 calendar days, the per-content-type Views
    /// inputs for median calculation, and content-count composition inputs.
    /// </summary>
    Task<WinningContentData> BuildAsync(WinningContentFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>Selected period for the Winning Content report. Dates are inclusive.</summary>
public sealed class WinningContentFilter
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    /// <summary>
    /// When false (default, matching Content Log), archived NON_KK/KK rows are excluded.
    /// AUTO_GMV_LIVE rows are NEVER excluded by the archive flag - locked rule 2 keeps
    /// them in Leaderboard, Below Median and Composition for their period.
    /// </summary>
    public bool IncludeArchived { get; set; }
}

/// <summary>One content item in the selected period with its latest metric and temporary ER.</summary>
public sealed class WinningContentItem
{
    public long ContentLogId { get; init; }
    public string VideoId { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Username { get; init; }
    public string? VideoUrl { get; init; }
    public DateTime? VideoPostTime { get; init; }

    public int? ContentTypeId { get; init; }
    public string? ContentTypeCode { get; init; }
    public string? ContentTypeName { get; init; }

    public long? Views { get; init; }
    public long? Likes { get; init; }
    public long? Comments { get; init; }
    public long? Shares { get; init; }

    /// <summary>Temporary ER = (Likes + Comments + Shares) / Views; NULL when undefined.</summary>
    public decimal? EngagementRate { get; init; }

    /// <summary>
    /// Baseline ER of THIS item's content type over the 30 calendar days before the
    /// period start (AVG of per-video ER). NULL when the type has no usable baseline.
    /// </summary>
    public decimal? BaselineEngagementRate { get; init; }

    /// <summary>
    /// Multiplier = EngagementRate / BaselineEngagementRate (same content type),
    /// unrounded. NULL when ER is NULL or the baseline is NULL/0. Information only -
    /// never an eligibility filter (locked rule 10).
    /// </summary>
    public decimal? Multiplier { get; init; }

    /// <summary>CapturedAt of the latest ContentMetric row used (NULL when the log has no metric yet).</summary>
    public DateTime? LatestMetricCapturedAt { get; init; }
}

/// <summary>
/// One leaderboard entry (Phase 2B): a ranked copy of a selected-period item with its
/// 1-based rank. Ranking is per content type, ER DESC then ContentLogId DESC; there is
/// NO ER/multiplier threshold. Multiplier is information only.
/// </summary>
public sealed class WinningContentLeaderboardEntry
{
    /// <summary>1-based rank within the content type (1 = highest ER).</summary>
    public int Rank { get; init; }

    public long ContentLogId { get; init; }
    public string VideoId { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Username { get; init; }
    public string? VideoUrl { get; init; }
    public DateTime? VideoPostTime { get; init; }

    public int? ContentTypeId { get; init; }
    public string? ContentTypeCode { get; init; }
    public string? ContentTypeName { get; init; }

    public long? Views { get; init; }
    public long? Likes { get; init; }
    public long? Comments { get; init; }
    public long? Shares { get; init; }

    public decimal? EngagementRate { get; init; }
    public decimal? BaselineEngagementRate { get; init; }

    /// <summary>Unrounded ER / baseline. Information only; never used for eligibility.</summary>
    public decimal? Multiplier { get; init; }

    public DateTime? LatestMetricCapturedAt { get; init; }
}

/// <summary>Leaderboard of one content type: its baseline plus the ranked Top-N entries.</summary>
public sealed class WinningContentLeaderboard
{
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;

    /// <summary>Baseline ER of this category (NULL when no usable baseline videos).</summary>
    public decimal? BaselineEngagementRate { get; init; }

    /// <summary>Number of usable baseline videos behind the baseline ER (0 when NULL baseline).</summary>
    public int BaselineVideoCount { get; init; }

    public IReadOnlyList<WinningContentLeaderboardEntry> Entries { get; init; } = Array.Empty<WinningContentLeaderboardEntry>();
}

/// <summary>Per-content-type baseline over the 30 calendar days before the selected period start.</summary>
public sealed class WinningContentBaseline
{
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;

    /// <summary>Baseline window: [SelectedPeriodStart - 30 days, SelectedPeriodStart). Calendar days.</summary>
    public DateTime BaselineStart { get; init; }
    public DateTime BaselineEndExclusive { get; init; }

    /// <summary>Number of baseline videos that produced a usable (non-NULL) ER.</summary>
    public int VideoCount { get; init; }

    /// <summary>AVG of each baseline video's individual ER (NOT aggregate likes/views). NULL when no video has a usable ER.</summary>
    public decimal? AverageEngagementRate { get; init; }
}

/// <summary>
/// Views values for one content type in the selected period - the exact input set for
/// MedianViews(ContentType). Median must be computed from these values (Phase 2F);
/// the service deliberately does not approximate it with AVG.
/// </summary>
public sealed class WinningContentMedianInput
{
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;

    /// <summary>Per-video Views of the selected period for this content type (latest metric per video; NULL Views preserved as NULL).</summary>
    public IReadOnlyList<long?> Views { get; init; } = Array.Empty<long?>();
}

/// <summary>Content-count composition inputs per content type (locked rule 8: counts, not Views).</summary>
public sealed class WinningContentCompositionInput
{
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>
/// Complete Phase 2A dataset for the Winning Content report. Presentation-only values
/// (percentages, medians, multipliers, leaderboard ranking) are deliberately NOT
/// computed here - Phase 2B/2F consume these inputs.
/// </summary>
public sealed class WinningContentData
{
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }

    /// <summary>Baseline window: [BaselineStart, BaselineEndExclusive) = 30 calendar days before StartDate.</summary>
    public DateTime BaselineStart { get; init; }
    public DateTime BaselineEndExclusive { get; init; }

    /// <summary>Selected-period content with latest metric + temporary ER (NON_KK, KK, AUTO_GMV_LIVE).</summary>
    public IReadOnlyList<WinningContentItem> Items { get; init; } = Array.Empty<WinningContentItem>();

    /// <summary>Per-content-type baseline over the preceding 30 calendar days.</summary>
    public IReadOnlyList<WinningContentBaseline> Baselines { get; init; } = Array.Empty<WinningContentBaseline>();

    /// <summary>Per-content-type Views inputs for median calculation (Phase 2F).</summary>
    public IReadOnlyList<WinningContentMedianInput> MedianInputs { get; init; } = Array.Empty<WinningContentMedianInput>();

    /// <summary>Per-content-type content counts for composition (Phase 2B/2F).</summary>
    public IReadOnlyList<WinningContentCompositionInput> Composition { get; init; } = Array.Empty<WinningContentCompositionInput>();

    /// <summary>
    /// Per-content-type leaderboards (Phase 2B): NON_KK Top 4, KK Top 4,
    /// AUTO_GMV_LIVE Top 2 by ER DESC (tie-break ContentLogId DESC). Ranking only -
    /// no ER/multiplier threshold, multiplier is information only.
    /// </summary>
    public IReadOnlyList<WinningContentLeaderboard> Leaderboards { get; init; } = Array.Empty<WinningContentLeaderboard>();

    /// <summary>
    /// Per-content-type exact median Views (Phase 2C): NON_KK, KK and AUTO_GMV_LIVE
    /// each get their own median; never combined across categories.
    /// </summary>
    public IReadOnlyList<WinningContentMedian> Medians { get; init; } = Array.Empty<WinningContentMedian>();    /// <summary>
    /// Per-content-type Below Median sections (Phase 2C): content whose latest-metric
    /// Views is STRICTLY below its own category's median Views. Categories are never
    /// combined; NULL-Views content is never included.
    /// </summary>
    public IReadOnlyList<WinningContentBelowMedianGroup> BelowMedian { get; init; } = Array.Empty<WinningContentBelowMedianGroup>();

    /// <summary>
    /// Content composition summary (Phase 2D): per-category content COUNTS with exact
    /// percentages over the three-category total. Percentages are NULL and HasData is
    /// false when the total population is 0 (explicit no-data state, never 0% splits).
    /// </summary>
    public WinningContentComposition CompositionSummary { get; init; } = WinningContentCalculator.ComputeComposition(0, 0, 0);
}

/// <summary>Aggregate content composition for the selected period (Phase 2D).</summary>
public sealed class WinningContentComposition
{
    public int NonKkCount { get; init; }

    /// <summary>Exact NonKkCount / TotalCount x 100 (unrounded). NULL when TotalCount = 0.</summary>
    public decimal? NonKkPercentage { get; init; }

    public int KkCount { get; init; }

    /// <summary>Exact KkCount / TotalCount x 100 (unrounded). NULL when TotalCount = 0.</summary>
    public decimal? KkPercentage { get; init; }

    public int AutoGmvLiveCount { get; init; }

    /// <summary>Exact AutoGmvLiveCount / TotalCount x 100 (unrounded). NULL when TotalCount = 0.</summary>
    public decimal? AutoGmvLivePercentage { get; init; }

    /// <summary>NonKkCount + KkCount + AutoGmvLiveCount (Auto GMV Live always included).</summary>
    public int TotalCount { get; init; }

    /// <summary>False only when TotalCount = 0 - the explicit "no content in period" state.</summary>
    public bool HasData { get; init; }

    /// <summary>Per-category slots in locked display order: NON_KK, KK, AUTO_GMV_LIVE (all three always present).</summary>
    public IReadOnlyList<WinningContentCompositionCategory> Categories { get; init; } = Array.Empty<WinningContentCompositionCategory>();
}

/// <summary>One composition category slot (Phase 2D). Zero-count categories stay present for the UI.</summary>
public sealed class WinningContentCompositionCategory
{
    public string ContentTypeCode { get; init; } = string.Empty;
    public int Count { get; init; }

    /// <summary>Exact Count / TotalCount x 100 (unrounded); NULL only when TotalCount = 0.</summary>
    public decimal? Percentage { get; init; }
}

/// <summary>Exact median Views for one content type over the selected period (Phase 2C).</summary>
public sealed class WinningContentMedian
{
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;

    /// <summary>Exact median of valid (non-NULL) Views; NULL when the type has no valid Views.</summary>
    public decimal? MedianViews { get; init; }

    /// <summary>Number of valid (non-NULL) Views values in the median population.</summary>
    public int ValidViewsCount { get; init; }

    /// <summary>Selected-period content rows of this type whose Views was NULL (excluded from the median population).</summary>
    public int NullViewsCount { get; init; }
}

/// <summary>One entry in a Below Median section (Phase 2C): the content fields the future UI needs.</summary>
public sealed class WinningContentBelowMedianEntry
{
    public long ContentLogId { get; init; }
    public string VideoId { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Username { get; init; }
    public string? VideoUrl { get; init; }
    public DateTime? VideoPostTime { get; init; }

    public int? ContentTypeId { get; init; }
    public string? ContentTypeCode { get; init; }
    public string? ContentTypeName { get; init; }

    /// <summary>Latest-metric Views of this content (NULL Views never enters this section).</summary>
    public long? Views { get; init; }

    /// <summary>Median Views of this entry's content type (strict comparison reference).</summary>
    public decimal? MedianViews { get; init; }

    public DateTime? LatestMetricCapturedAt { get; init; }
}

/// <summary>Below Median section of one content type (Phase 2C).</summary>
public sealed class WinningContentBelowMedianGroup
{
    public int ContentTypeId { get; init; }
    public string ContentTypeCode { get; init; } = string.Empty;

    /// <summary>Median Views of this category (NULL when the category has no valid Views).</summary>
    public decimal? MedianViews { get; init; }

    /// <summary>Selected-period content of this type with Views STRICTLY below the category median.</summary>
    public IReadOnlyList<WinningContentBelowMedianEntry> Entries { get; init; } = Array.Empty<WinningContentBelowMedianEntry>();
}
