using System;

namespace KaleContentOps.ViewModels;

/// <summary>
/// Team Performance read model (Phase 3 UI foundation).
/// All business calculation happens in <c>ITeamPerformanceService</c>; this type only
/// carries the finished values the Razor view renders. The view performs display
/// formatting ONLY and never recomputes a metric.
///
/// Locked business rules reflected in the shape of this contract:
/// - archived ContentLogs keep counting as historical operational workload (rule 1),
/// - <c>VideoPostTime</c> is the performance period, never CreatedAt/UpdatedAt (rule 4),
/// - <c>PicId == null</c> ("Belum Diisi") is team workload only and never an
///   individual PIC row (rules 7/10),
/// - a PIC's CURRENT <c>IsActive</c> is not historical eligibility (rules 2/3/8):
///   <see cref="TeamPerformancePicRow.IsActive"/> is informational only.
/// </summary>
public sealed class TeamPerformanceViewModel
{
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;

    // ---- Team totals (every content log in the period, archived included) ----
    public int TotalContentCount { get; init; }
    public long TotalViews { get; init; }

    // ---- Assigned vs "Belum Diisi" (PicId NULL) ----
    public int AssignedContentCount { get; init; }
    public long AssignedViews { get; init; }

    /// <summary>"Belum Diisi" team workload: content with no PIC assignment. Never a PIC row.</summary>
    public int UnassignedContentCount { get; init; }
    public long UnassignedViews { get; init; }

    // ---- Coverage / fair denominator ("Target Adil", locked rule 6) ----
    /// <summary>Eligible PICs for the period - the fair denominator, including PICs without content.</summary>
    public int EligiblePicCount { get; init; }

    /// <summary>Eligible PICs that actually produced content in the period.</summary>
    public int PicsWithContentCount { get; init; }

    /// <summary>
    /// Phase 3A: JoinDate/ResignDate are integrated in <c>TeamPerformanceService</c>, so
    /// employment-period data IS available and eligibility uses the real overlap rule.
    /// Kept as an explicit flag so the provisional "denominator not final" notice can be
    /// re-surfaced (see the view) if the employment integration is ever rolled back.
    /// </summary>
    public bool EmploymentPeriodDataAvailable { get; init; }

    public IReadOnlyList<TeamPerformancePicRow> Pics { get; init; } = Array.Empty<TeamPerformancePicRow>();

    /// <summary>True when the selected period contains at least one dated content log.</summary>
    public bool HasData => TotalContentCount > 0;

    public bool HasEligiblePics => EligiblePicCount > 0;
}

/// <summary>
/// One PIC row: actual operational workload for the reporting period plus its share of
/// the team totals. Rows are the fair denominator (eligible PICs), so a PIC with no
/// content still appears with zeros and <see cref="Rank"/> null.
/// </summary>
public sealed class TeamPerformancePicRow
{
    /// <summary>
    /// 1-based rank among PICs WITH content, ordered by TotalViews DESC, then
    /// ContentCount DESC, then name ASC. Null when the PIC produced no content.
    /// </summary>
    public int? Rank { get; init; }

    public int PicId { get; init; }
    public string PicName { get; init; } = string.Empty;

    /// <summary>Current MasterPic status only - NOT the historical period eligibility signal.</summary>
    public bool IsActive { get; init; }

    public int ContentCount { get; init; }
    public long TotalViews { get; init; }

    /// <summary>TotalViews / ContentCount; null when the PIC has no content (never a fabricated 0).</summary>
    public decimal? AverageViewsPerContent { get; init; }

    /// <summary>Share of the team content total; null when the team posted nothing.</summary>
    public decimal? ContentSharePercent { get; init; }

    /// <summary>Share of the team views total; null when the team has no views.</summary>
    public decimal? ViewsSharePercent { get; init; }

    public bool HasContent => ContentCount > 0;
}
