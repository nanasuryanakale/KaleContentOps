using System.Collections.Generic;

namespace KaleContentOps.Services.TikTok;

public interface ITikTokSignatureService
{
    /// <summary>
    /// Generate signature for a TikTok request according to the TikTok Shop signature algorithm.
    /// Parameters:
    /// - httpMethod: HTTP method (GET/POST) (not used in current algorithm but kept for future compatibility)
    /// - path: exact request path, e.g. /authorization/202309/shops
    /// - queryParams: all query parameters (method will exclude 'sign' and 'access_token')
    /// - bodyBytes: exact request body bytes if applicable; pass null if no body or multipart/form-data
    /// - contentType: request Content-Type header value (used to detect multipart/form-data)
    /// Returns the lowercase hex HMAC-SHA256 digest as required by TikTok Shop API.
    /// </summary>
    string GenerateSignature(
        string httpMethod,
        string path,
        IDictionary<string, string?> queryParams,
        byte[]? bodyBytes,
        string? contentType);
}

