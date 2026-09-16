using KaleContentOps.Models;
using System.Collections.Generic;

namespace KaleContentOps.ViewModels;

public class ContentLogIndexViewModel
{
    public List<ContentLog> Items { get; set; } = new List<ContentLog>();

    public int CurrentPage { get; set; }

    public int PageSize { get; set; }

    public int TotalItems { get; set; }

    public int TotalPages { get; set; }

    public string Search { get; set; } = string.Empty;

    public string SelectedContentType { get; set; } = string.Empty;

    public List<ContentType> ContentTypes { get; set; } = new List<ContentType>();

    public List<MasterPic> MasterPics { get; set; } = new List<MasterPic>();

    // historical/inactive pics referenced by the displayed items (only added when necessary)
    public List<MasterPic> HistoricalPics { get; set; } = new List<MasterPic>();

    public List<ProductionMethod> ProductionMethods { get; set; } = new List<ProductionMethod>();
}
