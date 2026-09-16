using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

public class TikTokTokenResponse
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    // Unix timestamp or offset in seconds
    public long? ExpiresIn { get; set; }
    public long? RefreshExpiresIn { get; set; }
    public string? OpenId { get; set; }
    public string? SellerName { get; set; }
}

public class TikTokAuthService : ITikTokAuthService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TikTokOptions _options;
    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<TikTokAuthService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TikTokAuthService(
        IHttpClientFactory httpFactory,
        Microsoft.Extensions.Options.IOptions<TikTokOptions> options,
        AppDbContext db,
        IDataProtectionProvider dataProtection,
        ILogger<TikTokAuthService>? logger = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _db = db;
        _protector = dataProtection.CreateProtector("TikTokAuthService.v1");
        _logger = logger ?? NullLogger<TikTokAuthService>.Instance;
    }

    private static DateTime ConvertUnixOrOffsetToUtc(long value)
    {
        // If value looks like a Unix timestamp (>= 1e9), interpret as seconds since epoch
        if (value > 1000000000L)
        {
            return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
        }

        // Otherwise treat as seconds offset from now
        return DateTime.UtcNow.AddSeconds(value);
    }

    public async Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AppKey) || string.IsNullOrWhiteSpace(_options.AppSecret))
            throw new InvalidOperationException("TikTok AppKey/AppSecret not configured. Set via user secrets or environment variables.");

        if (string.IsNullOrWhiteSpace(authCode))
            throw new ArgumentException("authCode is required", nameof(authCode));

        var client = _httpFactory.CreateClient("TikTokAuth");

        var query = new Dictionary<string, string?>
        {
            { "app_key", _options.AppKey },
            { "app_secret", _options.AppSecret },
            { "auth_code", authCode },
            { "grant_type", "authorized_code" }
        };

        var url = QueryHelpers.AddQueryString("/api/v2/token/get", query);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await client.SendAsync(req, cancellationToken);

        var content = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            // Do not log secrets
            throw new HttpRequestException($"Token exchange failed: {res.StatusCode}");
        }

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // Validate code == 0
        var code = root.TryGetProperty("code", out var codeElem) && codeElem.TryGetInt32(out var codeVal) ? codeVal : 0;
        var requestId = root.GetPropertyOrDefault("request_id");
        if (code != 0)
        {
            var message = root.GetPropertyOrDefault("message") ?? "TikTok token exchange failed";
            // Do not include secrets in the exception
            throw new TikTokAuthException($"TikTok token exchange error: {message}", code, requestId);
        }

        var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

        var response = new TikTokTokenResponse
        {
            AccessToken = data.GetPropertyOrDefault("access_token"),
            RefreshToken = data.GetPropertyOrDefault("refresh_token"),
            ExpiresIn = data.GetPropertyOrDefaultLong("access_token_expire_in") ?? data.GetPropertyOrDefaultLong("expires_in"),
            RefreshExpiresIn = data.GetPropertyOrDefaultLong("refresh_token_expire_in") ?? data.GetPropertyOrDefaultLong("refresh_expires_in"),
            OpenId = data.GetPropertyOrDefault("open_id"),
            SellerName = data.GetPropertyOrDefault("seller_name")
        };

        var sellerRegion = data.GetPropertyOrDefault("seller_base_region");
        var userType = data.GetPropertyOrDefault("user_type");
        var grantedScopes = data.GetPropertyOrDefault("granted_scopes");

        // Safe diagnostic logging: do NOT log tokens or secrets.
        if (string.IsNullOrWhiteSpace(grantedScopes))
        {
            _logger.LogWarning("TikTok token response missing granted_scopes. code={Code} request_id={RequestId} user_type={UserType} seller_base_region={SellerRegion}", code, requestId, userType, sellerRegion);
        }
        else
        {
            _logger.LogInformation("TikTok token response: code={Code} request_id={RequestId} granted_scopes={GrantedScopes} user_type={UserType} seller_base_region={SellerRegion}", code, requestId, grantedScopes, userType, sellerRegion);
        }

        // Validate required scope
        if (!string.IsNullOrWhiteSpace(grantedScopes))
        {
            // granted_scopes may be comma-separated or space-separated; perform simple contains check
            if (!grantedScopes.Contains("data.shop_analytics.public.read"))
            {
                throw new TikTokAuthException("Required scope data.shop_analytics.public.read is missing from granted_scopes", code, requestId);
            }
        }

        // Persist credential securely
        var existing = await _db.TikTokCredentials.FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (existing == null)
        {
            existing = new TikTokCredential { AppKey = _options.AppKey };
            _db.TikTokCredentials.Add(existing);
        }

        existing.EncryptedAccessToken = response.AccessToken != null ? _protector.Protect(response.AccessToken) : null;
        existing.EncryptedRefreshToken = response.RefreshToken != null ? _protector.Protect(response.RefreshToken) : null;

        if (response.ExpiresIn.HasValue)
        {
            existing.ExpiresAt = ConvertUnixOrOffsetToUtc(response.ExpiresIn.Value);
        }
        else
        {
            existing.ExpiresAt = null;
        }

        if (response.RefreshExpiresIn.HasValue)
        {
            existing.RefreshExpiresAt = ConvertUnixOrOffsetToUtc(response.RefreshExpiresIn.Value);
        }
        else
        {
            existing.RefreshExpiresAt = null;
        }

        existing.OpenId = response.OpenId;
        existing.SellerName = response.SellerName;
        existing.Region = sellerRegion ?? existing.Region;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return response;
    }

    public async Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AppKey) || string.IsNullOrWhiteSpace(_options.AppSecret))
            throw new InvalidOperationException("TikTok AppKey/AppSecret not configured. Set via user secrets or environment variables.");

        if (credential == null) return null;

        string? refreshToken = null;
        if (!string.IsNullOrWhiteSpace(credential.EncryptedRefreshToken))
        {
            try
            {
                refreshToken = _protector.Unprotect(credential.EncryptedRefreshToken);
            }
            catch (Exception ex)
            {
                // Do NOT log token values. Log safe diagnostic information for production debugging.
                _logger.LogWarning(ex, "Data Protection unprotect failed for TikTok refresh token for credential {CredentialId}: {ExceptionType}: {Message}", credential.Id, ex.GetType().FullName, ex.Message);
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        var client = _httpFactory.CreateClient("TikTokAuth");

        var query = new Dictionary<string, string?>
        {
            { "app_key", _options.AppKey },
            { "app_secret", _options.AppSecret },
            { "refresh_token", refreshToken },
            { "grant_type", "refresh_token" }
        };

        var url = QueryHelpers.AddQueryString("/api/v2/token/refresh", query);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await client.SendAsync(req, cancellationToken);

        var content = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Token refresh failed: {res.StatusCode}");
        }

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var code = root.TryGetProperty("code", out var codeElem) && codeElem.TryGetInt32(out var codeVal) ? codeVal : 0;
        var requestId = root.GetPropertyOrDefault("request_id");
        if (code != 0)
        {
            var message = root.GetPropertyOrDefault("message") ?? "TikTok token refresh failed";
            throw new TikTokAuthException($"TikTok token refresh error: {message}", code, requestId);
        }

        var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

        var response = new TikTokTokenResponse
        {
            AccessToken = data.GetPropertyOrDefault("access_token"),
            RefreshToken = data.GetPropertyOrDefault("refresh_token"),
            ExpiresIn = data.GetPropertyOrDefaultLong("access_token_expire_in") ?? data.GetPropertyOrDefaultLong("expires_in"),
            RefreshExpiresIn = data.GetPropertyOrDefaultLong("refresh_token_expire_in") ?? data.GetPropertyOrDefaultLong("refresh_expires_in"),
            OpenId = data.GetPropertyOrDefault("open_id"),
            SellerName = data.GetPropertyOrDefault("seller_name")
        };

        var sellerRegion = data.GetPropertyOrDefault("seller_base_region");
        var userType = data.GetPropertyOrDefault("user_type");
        var grantedScopes = data.GetPropertyOrDefault("granted_scopes");

        // Safe diagnostic logging for token refresh response (do NOT log tokens)
        if (string.IsNullOrWhiteSpace(grantedScopes))
        {
            _logger.LogWarning("TikTok token refresh response missing granted_scopes. code={Code} request_id={RequestId} user_type={UserType} seller_base_region={SellerRegion}", code, requestId, userType, sellerRegion);
        }
        else
        {
            _logger.LogInformation("TikTok token refresh response: code={Code} request_id={RequestId} granted_scopes={GrantedScopes} user_type={UserType} seller_base_region={SellerRegion}", code, requestId, grantedScopes, userType, sellerRegion);
        }

        if (!string.IsNullOrWhiteSpace(grantedScopes) && !grantedScopes.Contains("data.shop_analytics.public.read"))
        {
            throw new TikTokAuthException("Required scope data.shop_analytics.public.read is missing from granted_scopes", code, requestId);
        }

        credential.EncryptedAccessToken = response.AccessToken != null ? _protector.Protect(response.AccessToken) : credential.EncryptedAccessToken;
        credential.EncryptedRefreshToken = response.RefreshToken != null ? _protector.Protect(response.RefreshToken) : credential.EncryptedRefreshToken;
        if (response.ExpiresIn.HasValue)
            credential.ExpiresAt = ConvertUnixOrOffsetToUtc(response.ExpiresIn.Value);
        if (response.RefreshExpiresIn.HasValue)
            credential.RefreshExpiresAt = ConvertUnixOrOffsetToUtc(response.RefreshExpiresIn.Value);
        credential.OpenId = response.OpenId ?? credential.OpenId;
        credential.SellerName = response.SellerName ?? credential.SellerName;
        credential.Region = sellerRegion ?? credential.Region;
        credential.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return response;
    }

    public async Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default)
    {
        var credential = await _db.TikTokCredentials.FindAsync(new object[] { credentialId }, cancellationToken);
        if (credential == null) throw new ArgumentException("Credential not found", nameof(credentialId));
        return await RefreshTokenAsync(credential, cancellationToken);
    }

    public async Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default)
    {
        var credential = await _db.TikTokCredentials.FindAsync(new object[] { credentialId }, cancellationToken);
        if (credential == null) return null;

        // If token is missing or about to expire within safety window, refresh
        var safetyWindow = TimeSpan.FromSeconds(60);
        if (string.IsNullOrWhiteSpace(credential.EncryptedAccessToken) || !credential.ExpiresAt.HasValue || credential.ExpiresAt.Value <= DateTime.UtcNow.Add(safetyWindow))
        {
            await RefreshTokenAsync(credential, cancellationToken);
            // reload credential after refresh
            await _db.Entry(credential).ReloadAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(credential.EncryptedAccessToken)) return null;
        try
        {
            return _protector.Unprotect(credential.EncryptedAccessToken);
        }
        catch (Exception ex)
        {
            // Do NOT log token values. Log safe diagnostic information for production debugging.
            _logger.LogWarning(ex, "Data Protection unprotect failed for TikTok access token for credential {CredentialId}: {ExceptionType}: {Message}", credential.Id, ex.GetType().FullName, ex.Message);
            return null;
        }
    }
}
