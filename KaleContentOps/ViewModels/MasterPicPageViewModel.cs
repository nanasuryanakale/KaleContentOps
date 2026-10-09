using KaleContentOps.Models;
using System;
using System.Collections.Generic;

namespace KaleContentOps.ViewModels;

/// <summary>
/// Phase 2B - Master PIC management page model.
/// BusinessToday is the Asia/Jakarta business date from IShopTimeZone and is the ONLY
/// source used for default JoinDate values and the "no future JoinDate" validation rule.
/// It must never be derived from the browser/server local clock.
/// </summary>
public class MasterPicPageViewModel
{
    /// <summary>All Master PICs (active AND inactive) - historical visibility is required.</summary>
    public List<MasterPic> Pics { get; set; } = new List<MasterPic>();

    /// <summary>Current business date (Asia/Jakarta).</summary>
    public DateOnly BusinessToday { get; set; }
}
