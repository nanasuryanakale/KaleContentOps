using System.Text.Json;

namespace KaleContentOps.Services;

public static class JsonExtensions
{
    public static string? GetPropertyOrDefault(this JsonElement elem, string propName)
    {
        if (elem.ValueKind == JsonValueKind.Undefined || elem.ValueKind == JsonValueKind.Null)
            return null;
        if (elem.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Null) return null;
            return prop.ToString();
        }
        return null;
    }

    public static int? GetPropertyOrDefaultInt(this JsonElement elem, string propName)
    {
        if (elem.ValueKind == JsonValueKind.Undefined || elem.ValueKind == JsonValueKind.Null)
            return null;
        if (elem.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v)) return v;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var v2)) return v2;
        }
        return null;
    }

    public static long? GetPropertyOrDefaultLong(this JsonElement elem, string propName)
    {
        if (elem.ValueKind == JsonValueKind.Undefined || elem.ValueKind == JsonValueKind.Null)
            return null;
        if (elem.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var v)) return v;
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var v2)) return v2;
        }
        return null;
    }
}
