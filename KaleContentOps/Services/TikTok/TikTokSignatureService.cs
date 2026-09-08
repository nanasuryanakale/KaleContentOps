using System.Collections.Generic;
using System.Linq;

namespace KaleContentOps.Services.TikTok;

public class TikTokSignatureService : ITikTokSignatureService
{
    private readonly TikTokOptions _options;

    public TikTokSignatureService(Microsoft.Extensions.Options.IOptions<TikTokOptions> options)
    {
        _options = options.Value;
    }

    public string GenerateSignature(string httpMethod, string path, IDictionary<string, string?> queryParams, byte[]? bodyBytes, string? contentType)
    {
        // Implementation follows the official TikTok Shop signing rules provided.
        // Steps implemented:
        // 1. Exclude query params with name 'sign' and 'access_token' (case-insensitive)
        // 2. Sort remaining query parameter names alphabetically (ordinal)
        // 3. Concatenate each param as key + value with no separators
        // 4. Prepend the exact path
        // 5. If contentType is not multipart/form-data and bodyBytes present, append exact body bytes
        // 6. Wrap with app_secret: app_secret + sign_string + app_secret
        // 7. Compute HMAC-SHA256 using key = app_secret
        // 8. Return lowercase hex digest

        var appSecret = _options.AppSecret ?? string.Empty;

        // Build canonical query string: exclude sign and access_token
        var kv = queryParams ?? new Dictionary<string, string?>();
        var filtered = kv
            .Where(p => !string.Equals(p.Key, "sign", System.StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(p.Key, "access_token", System.StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value);

        var orderedKeys = filtered.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToList();

        // Use a MemoryStream to assemble bytes exactly
        using var ms = new System.IO.MemoryStream();
        // prepend path bytes
        var utf8 = System.Text.Encoding.UTF8;
        var pathBytes = utf8.GetBytes(path ?? string.Empty);
        ms.Write(pathBytes, 0, pathBytes.Length);

        // append concatenated key+value (values may be null -> treat as empty string)
        foreach (var key in orderedKeys)
        {
            var val = filtered[key] ?? string.Empty;
            var kvBytes = utf8.GetBytes(string.Concat(key, val));
            ms.Write(kvBytes, 0, kvBytes.Length);
        }

        // Append body bytes if applicable
        if (bodyBytes != null && !IsMultipartFormData(contentType))
        {
            ms.Write(bodyBytes, 0, bodyBytes.Length);
        }

        // Now build wrapped bytes: app_secret + ms.ToArray() + app_secret
        var prefix = utf8.GetBytes(appSecret);
        var suffix = prefix; // same bytes

        using var finalMs = new System.IO.MemoryStream();
        finalMs.Write(prefix, 0, prefix.Length);
        var middle = ms.ToArray();
        finalMs.Write(middle, 0, middle.Length);
        finalMs.Write(suffix, 0, suffix.Length);

        var message = finalMs.ToArray();

        // HMAC-SHA256 with key = app_secret (UTF8 bytes)
        var keyBytes = utf8.GetBytes(appSecret);
        using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(message);

        // return lowercase hex
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static bool IsMultipartFormData(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        return contentType.StartsWith("multipart/form-data", System.StringComparison.OrdinalIgnoreCase);
    }
}
