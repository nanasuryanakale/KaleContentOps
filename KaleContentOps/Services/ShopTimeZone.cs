using Microsoft.Extensions.Options;

namespace KaleContentOps.Services;

/// <summary>
/// Configuration for the shop reporting timezone (Issue A + B single timezone source).
/// Configured via the "ShopTimeZone" section, e.g. { "TimeZoneId": "Asia/Jakarta" }.
/// When the application later supports multiple shops/timezones, replace this single
/// id with per-shop resolution without changing the IShopTimeZone contract.
/// </summary>
public class ShopTimeZoneOptions
{
    public const string SectionName = "ShopTimeZone";

    /// <summary>Default IANA timezone id (current shop: Indonesia / GMT+7).</summary>
    public const string DefaultTimeZoneId = "Asia/Jakarta";

    /// <summary>IANA timezone id of the shop's registered timezone, e.g. "Asia/Jakarta".</summary>
    public string TimeZoneId { get; set; } = DefaultTimeZoneId;
}

/// <summary>
/// Single shared source for the shop registered timezone (currently Asia/Jakarta, GMT+7).
/// Used for:
///  - Issue A: Daily Summary default "today" (must follow the shop reporting timezone)
///  - Issue B: TikTok API start_date_ge / end_date_lt which TikTok defines as
///    ISO dates in the shop registered timezone.
/// Always converts explicitly from the current UTC instant; never uses
/// DateTime.Now or the server machine timezone.
/// </summary>
public interface IShopTimeZone
{
    /// <summary>IANA timezone id currently configured.</summary>
    string TimeZoneId { get; }

    /// <summary>Converts a UTC instant to the configured shop timezone.</summary>
    DateTimeOffset ToShopLocal(DateTimeOffset utcInstant);

    /// <summary>Shop-local calendar date for a UTC instant.</summary>
    DateOnly GetShopLocalDate(DateTimeOffset utcInstant);

    /// <summary>Shop-local calendar date for the current instant.</summary>
    DateOnly Today();

    /// <summary>Shop-local midnight (DateTime at 00:00) for the given shop-local calendar date.</summary>
    DateTime ToDateTime(DateOnly shopLocalDate);

    /// <summary>Shop-local "today" at midnight. Equivalent to ToDateTime(Today()).</summary>
    DateTime TodayMidnight();
}

public sealed class ShopTimeZone : IShopTimeZone
{
    private readonly TimeZoneInfo _timeZone;

    public ShopTimeZone(IOptions<ShopTimeZoneOptions> options)
    {
        var id = string.IsNullOrWhiteSpace(options.Value?.TimeZoneId)
            ? ShopTimeZoneOptions.DefaultTimeZoneId
            : options.Value.TimeZoneId.Trim();
        TimeZoneId = id;
        _timeZone = ResolveTimeZone(id);
    }

    public string TimeZoneId { get; }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            // IANA ids work on Linux/macOS and on modern Windows (ICU-backed, .NET 6+).
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            // Fallback for older Windows environments: map IANA id to a Windows timezone id.
            try
            {
                if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) &&
                    TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out var windowsTz))
                {
                    return windowsTz;
                }
            }
            catch (NotSupportedException)
            {
                // IANA->Windows conversion unsupported on this platform; rethrow below.
            }

            throw;
        }
    }

    public DateTimeOffset ToShopLocal(DateTimeOffset utcInstant)
        => TimeZoneInfo.ConvertTime(utcInstant, _timeZone);

    public DateOnly GetShopLocalDate(DateTimeOffset utcInstant)
        => DateOnly.FromDateTime(ToShopLocal(utcInstant).Date);

    public DateOnly Today()
        => GetShopLocalDate(DateTimeOffset.UtcNow);

    public DateTime ToDateTime(DateOnly shopLocalDate)
        => shopLocalDate.ToDateTime(TimeOnly.MinValue);

    public DateTime TodayMidnight()
        => ToDateTime(Today());
}
