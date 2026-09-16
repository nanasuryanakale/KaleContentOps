using System;
using System.Collections.Generic;

namespace KaleContentOps.Services.TikTok
{
    public class DryRunVideoEntry
    {
        public string VideoId { get; set; } = string.Empty;
        public long? ContentLogId { get; set; }
        public int? ExistingContentTypeId { get; set; }
        public string? Title { get; set; }
        public string? Username { get; set; }
        public DateTime? VideoPostTime { get; set; }
        public string? AuthorType { get; set; }
        public bool ProductsAvailable { get; set; }
        public int ProductsCount { get; set; }
        public List<string> ProductNames { get; set; } = new List<string>();
        public decimal? GmvAmount { get; set; }
        public string? GmvCurrency { get; set; }
        public int? ItemsSold { get; set; }
        public int? SkuOrders { get; set; }
        public List<string> HashTags { get; set; } = new List<string>();
        public List<string> PropertyNames { get; set; } = new List<string>();
    }

    public class DryRunSummary
    {
        public int TotalReceived { get; set; }
        public int TotalMatched { get; set; }
        public int TotalUnmatched { get; set; }
        public int WithProductsCount { get; set; }
        public int WithoutProductsCount { get; set; }
        public int WithGmvCount { get; set; }
        public int WithItemsSoldCount { get; set; }
        public int WithSkuOrdersCount { get; set; }
    }

    public class DryRunReport
    {
        public List<DryRunVideoEntry> Entries { get; set; } = new List<DryRunVideoEntry>();
        public DryRunSummary Summary { get; set; } = new DryRunSummary();
        public Dictionary<string, DryRunVideoEntry> GroundTruthMatches { get; set; } = new Dictionary<string, DryRunVideoEntry>();
    }
}
