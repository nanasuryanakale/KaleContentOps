using System.Collections.Generic;
using System.Text.Json;

namespace KaleContentOps.Services.TikTok;

public class DetailsMetrics
{
    // primary engagement counts
    public long? Views { get; set; }
    public string? ViewsPath { get; set; }

    public long? Likes { get; set; }
    public string? LikesPath { get; set; }

    public long? Comments { get; set; }
    public string? CommentsPath { get; set; }

    public long? Shares { get; set; }
    public string? SharesPath { get; set; }

    public long? NewFollowers { get; set; }
    public string? NewFollowersPath { get; set; }

    // optional fields - may not be present in this endpoint
    public decimal? AverageWatch { get; set; }
    public string? AverageWatchPath { get; set; }

    public decimal? FullWatchRate { get; set; }
    public string? FullWatchRatePath { get; set; }

    public long? Reach { get; set; }
    public string? ReachPath { get; set; }

    // Demographics
    public decimal? Male { get; set; }
    public string? MalePath { get; set; }

    public decimal? Female { get; set; }
    public string? FemalePath { get; set; }

    public decimal? NoGender { get; set; }
    public string? NoGenderPath { get; set; }

    public decimal? Age18 { get; set; }
    public string? Age18Path { get; set; }

    public decimal? Age25 { get; set; }
    public string? Age25Path { get; set; }

    public decimal? Age35 { get; set; }
    public string? Age35Path { get; set; }

    public decimal? Age45 { get; set; }
    public string? Age45Path { get; set; }

    public decimal? Age55 { get; set; }
    public string? Age55Path { get; set; }

    // Fields not present will have null value and null path
}
