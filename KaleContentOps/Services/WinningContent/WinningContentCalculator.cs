namespace KaleContentOps.Services.WinningContent;

/// <summary>
/// Central, pure calculation helpers for Winning Content (Phase 2A data foundation).
/// Kept separate from the query service so Phase 2B can extend ER with Saves in exactly
/// one place, and so the rules are unit-testable without any database.
/// </summary>
public static class WinningContentCalculator
{
    /// <summary>
    /// Temporary Engagement Rate (locked rule 4, while Saves is unavailable):
    ///     ER = (Likes + Comments + Shares) / Views
    /// NULL semantics (rule: never fabricate, never divide-by-zero):
    /// - any missing numerator component (Likes/Comments/Shares == null) -> NULL
    /// - Views == null -> NULL
    /// - Views == 0 -> NULL (NULLIF(Views, 0))
    /// The single place Phase 2B extends to
    ///     ER = (Likes + Comments + Shares + COALESCE(Saves, 0)) / Views.
    /// </summary>
    public static decimal? ComputeEngagementRate(long? likes, long? comments, long? shares, long? views)
    {
        if (likes is null || comments is null || shares is null || views is null || views == 0)
        {
            return null;
        }

        return (likes.Value + comments.Value + shares.Value) / (decimal)views.Value;
    }

    /// <summary>
    /// Multiplier (locked rule 8):
    ///     Multiplier = SelectedPeriodER / BaselineER (same Content Type)
    /// NULL semantics (locked rule 9):
    /// - ER null -> NULL
    /// - baseline null (no usable baseline videos) -> NULL
    /// - baseline == 0 -> NULL (never divide by zero, never fabricate 1x)
    /// No rounding is applied - keep full precision internally; format for UI only later.
    /// The multiplier is INFORMATION ONLY - callers must never filter eligibility by it
    /// (locked rule 10: no minimum ER / multiplier threshold).
    /// </summary>
    public static decimal? ComputeMultiplier(decimal? engagementRate, decimal? baselineEngagementRate)
    {
        if (engagementRate is null || baselineEngagementRate is null || baselineEngagementRate == 0m)
        {
            return null;
        }

        return engagementRate.Value / baselineEngagementRate.Value;
    }

    /// <summary>
    /// Exact median of valid Views values (locked Phase 2C rules 2/6/7/8):
    /// - input may contain NULLs; NULLs are EXCLUDED from the median population
    ///   (never treated as zero, never fabricated)
    /// - odd count  -> the middle value after sorting ascending
    /// - even count -> the AVERAGE of the two middle values, computed in decimal so the
    ///   result never suffers integer truncation (e.g. [200, 500] -> 350, [1, 2] -> 1.5)
    /// - no valid (non-NULL) values -> NULL (never 0, never another category's median)
    /// NOT an AVG over all rows. Single authoritative median method - do not duplicate.
    /// </summary>
    public static decimal? ComputeMedianViews(IEnumerable<long?> views)
    {
        // GetValueOrDefault after the HasValue filter keeps this warning-free while the
        // semantics stay identical (nulls never reach the selector's output).
        var valid = (views ?? Enumerable.Empty<long?>())
            .Where(v => v.HasValue)
            .Select(v => v.GetValueOrDefault())
            .OrderBy(v => v)
            .ToList();
        if (valid.Count == 0)
        {
            return null;
        }

        var mid = valid.Count / 2;
        return valid.Count % 2 == 1
            ? valid[mid]
            : (valid[mid - 1] + valid[mid]) / 2m; // decimal division: no integer truncation
    }

    /// <summary>
    /// Below Median eligibility (locked Phase 2C rule 9): STRICT less-than.
    ///     Views < MedianViews(ContentType)
    /// - Views null -> false (content with no usable Views cannot satisfy the condition)
    /// - Views == median -> false (equal-to-median is EXCLUDED; never <=)
    /// - median null -> false (no usable median for the category)
    /// </summary>
    public static bool IsBelowMedian(long? views, decimal? medianViews)
    {
        return views.HasValue && medianViews.HasValue && views.Value < medianViews.Value;
    }

    /// <summary>
    /// Content Composition (Phase 2D, locked rules 2-7): percentages from CONTENT COUNTS,
    /// never from Views/ER/engagement. Single authoritative percentage calculation -
    /// do not duplicate the formula in services, controllers or UI.
    ///     Percentage = CategoryCount / (NonKk + Kk + AutoGmvLive) x 100
    /// - AUTO_GMV_LIVE is always part of the denominator (locked rule 4)
    /// - TotalCount == 0 -> all percentages NULL and HasData=false: an explicit
    ///   "no data" state - never 0%, never 33.33%, never a fabricated split (rule 7)
    /// - a zero-count category with data elsewhere -> exactly 0% (rule 8)
    /// Precision contract: percentages are kept at full decimal precision (no backend
    /// rounding). Display formatting (e.g. 66.666...% -> 67%) is the UI phase's job;
    /// note that independently rounded integers may sum to 99% or 101% - if the UI
    /// requires an exact-100 split it should apply a display-allocation (largest
    /// remainder) strategy at render time. No such algorithm belongs in the backend.
    /// </summary>
    public static WinningContentComposition ComputeComposition(int nonKkCount, int kkCount, int autoGmvLiveCount)
    {
        var total = nonKkCount + kkCount + autoGmvLiveCount;
        return new WinningContentComposition
        {
            NonKkCount = nonKkCount,
            NonKkPercentage = total == 0 ? null : nonKkCount / (decimal)total * 100m,
            KkCount = kkCount,
            KkPercentage = total == 0 ? null : kkCount / (decimal)total * 100m,
            AutoGmvLiveCount = autoGmvLiveCount,
            AutoGmvLivePercentage = total == 0 ? null : autoGmvLiveCount / (decimal)total * 100m,
            TotalCount = total,
            HasData = total > 0,
            Categories = new[]
            {
                new WinningContentCompositionCategory
                {
                    ContentTypeCode = "NON_KK",
                    Count = nonKkCount,
                    Percentage = total == 0 ? null : nonKkCount / (decimal)total * 100m
                },
                new WinningContentCompositionCategory
                {
                    ContentTypeCode = "KK",
                    Count = kkCount,
                    Percentage = total == 0 ? null : kkCount / (decimal)total * 100m
                },
                new WinningContentCompositionCategory
                {
                    ContentTypeCode = "AUTO_GMV_LIVE",
                    Count = autoGmvLiveCount,
                    Percentage = total == 0 ? null : autoGmvLiveCount / (decimal)total * 100m
                }
            }
        };
    }
}
