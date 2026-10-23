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

    // Country demographics from country_distribution[] of the first VIEWERS profile.
    // Key = ISO country code (blank codes skipped), value = viewer share 0..1 as returned by the API.
    // Null when the VIEWERS profile has no parseable country_distribution entries.
    public Dictionary<string, decimal?>? Countries { get; set; }

    public string? CountriesPath { get; set; }

    // --------------------------------------------------------------
    // NEW_FOLLOWER profile (separate population - NEVER substitutes VIEWERS fields).
    // Real payloads contain viewer_profile[type=="NEW_FOLLOWER"] with the same
    // gender/age/country distribution shapes. These are kept apart so both profiles
    // can coexist in one response without overwriting each other.
    // Missing data stays null; absent distributions are never fabricated as zero.
    // --------------------------------------------------------------
    public decimal? NfMale { get; set; }
    public string? NfMalePath { get; set; }

    public decimal? NfFemale { get; set; }
    public string? NfFemalePath { get; set; }

    public decimal? NfNoGender { get; set; }
    public string? NfNoGenderPath { get; set; }

    public decimal? NfAge18 { get; set; }
    public string? NfAge18Path { get; set; }

    public decimal? NfAge25 { get; set; }
    public string? NfAge25Path { get; set; }

    public decimal? NfAge35 { get; set; }
    public string? NfAge35Path { get; set; }

    public decimal? NfAge45 { get; set; }
    public string? NfAge45Path { get; set; }

    public decimal? NfAge55 { get; set; }
    public string? NfAge55Path { get; set; }

    public Dictionary<string, decimal?>? NfCountries { get; set; }

    public string? NfCountriesPath { get; set; }

    // Fields not present will have null value and null path
}
