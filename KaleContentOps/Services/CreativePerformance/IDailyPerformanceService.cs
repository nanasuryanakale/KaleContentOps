using System.Threading;
using System.Threading.Tasks;

namespace KaleContentOps.Services.CreativePerformance;

/// <summary>
/// Creative Performance - Daily Report (account-level analytics) service contract.
///
/// Responsibilities (and nothing else):
///  - validate the user-selected inclusive range (Asia/Jakarta calendar dates),
///  - derive the equal-length Previous Period (apples-to-apples, never overlapping),
///  - read the verified account-level source for both periods,
///  - compute every metric exactly once so cards, comparison table, charts and the
///    daily table are guaranteed to show the same numbers.
///
/// The controller owns no business math; the View owns no business math.
/// </summary>
public interface IDailyPerformanceService
{
    Task<DailyReportData> BuildAsync(DailyReportFilter filter, CancellationToken cancellationToken = default);
}
