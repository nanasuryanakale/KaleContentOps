using KaleContentOps.ViewModels;

namespace KaleContentOps.Services.TeamPerformance;

/// <summary>
/// Domain service for the Team Performance menu. Owns ALL business calculation:
/// period/date-range resolution, archived-inclusive ContentLog aggregation by PIC,
/// latest-metric resolution, PIC eligibility (fair denominator) and ranking.
/// Controllers and Razor views never compute a business metric.
/// </summary>
public interface ITeamPerformanceService
{
    /// <summary>
    /// Builds the Team Performance read model for an inclusive shop-local reporting
    /// period. Defaults/normalization of the period belong to the caller (controller);
    /// this service normalizes an inverted range defensively.
    /// </summary>
    Task<TeamPerformanceViewModel> BuildAsync(TeamPerformanceFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reporting-period input. Start/End are inclusive shop-local calendar dates
/// (Asia/Jakarta); the service converts them to the application's half-open
/// <c>VideoPostTime</c> window [start, end + 1 day).
/// </summary>
public sealed class TeamPerformanceFilter
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}
