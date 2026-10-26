using System.Threading;
using System.Threading.Tasks;

namespace KaleContentOps.Services.CreativePerformance;

/// <summary>
/// Creative Performance - Weekly Report (account-level analytics) service contract.
///
/// Responsibilities (and nothing else):
///  - validate the user-selected inclusive range (Asia/Jakarta calendar dates),
///  - derive the equal-length Previous Period (apples-to-apples, never overlapping),
///  - split BOTH periods into clipped Monday-Sunday buckets,
///  - read the verified account-level source once per bucket window (sequential),
///  - compute every metric exactly once so cards, comparison table, charts and the
///    weekly table are guaranteed to show the same numbers.
///
/// The controller owns no business math; the View owns no business math.
/// </summary>
public interface IWeeklyPerformanceService
{
    Task<WeeklyReportData> BuildAsync(WeeklyReportFilter filter, CancellationToken cancellationToken = default);
}
