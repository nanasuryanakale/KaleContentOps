using System.Threading;
using System.Threading.Tasks;

namespace KaleContentOps.Services.CreativePerformance;

/// <summary>
/// Creative Performance - Monthly Report (account-level analytics) service contract.
/// </summary>
public interface IMonthlyPerformanceService
{
    Task<MonthlyReportData> BuildAsync(MonthlyReportFilter filter, CancellationToken cancellationToken = default);
}