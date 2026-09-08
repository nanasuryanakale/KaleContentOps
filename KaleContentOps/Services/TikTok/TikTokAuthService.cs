using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Data;
using KaleContentOps.Models;

namespace KaleContentOps.Services.TikTok;

public class TikTokTokenResponse
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public int? ExpiresIn { get; set; }
    public int? RefreshExpiresIn { get; set; }
    public string? OpenId { get; set; }
    public string? SellerName { get; set; }
    // Additional fields from TikTok response should be added only after verification
}

public class TikTokAuthService : ITikTokAuthService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TikTokOptions _options;
    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TikTokAuthService(
        IHttpClientFactory httpFactory,
        Microsoft.Extensions.Options.IOptions<TikTokOptions> options,
        AppDbContext db,
        IDataProtectionProvider dataProtection)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _db = db;
        _protector = dataProtection.CreateProtector("TikTokAuthService.v1");
    }

    public async Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AppKey) || string.IsNullOrWhiteSpace(_options.AppSecret))
            throw new InvalidOperationException("TikTok AppKey/AppSecret not configured. Set via user secrets or environment variables.");

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
        // Parse tolerant structure: look for data object
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

        var response = new TikTokTokenResponse
        {
            AccessToken = data.GetPropertyOrDefault("access_token"),
            RefreshToken = data.GetPropertyOrDefault("refresh_token"),
            ExpiresIn = data.GetPropertyOrDefaultInt("expires_in"),
            RefreshExpiresIn = data.GetPropertyOrDefaultInt("refresh_expires_in"),
            OpenId = data.GetPropertyOrDefault("open_id"),
            SellerName = data.GetPropertyOrDefault("seller_name")
        };

        // Persist credential securely
        var existing = await _db.TikTokCredentials.FirstOrDefaultAsync(x => x.AppKey == _options.AppKey, cancellationToken);
        if (existing == null)
        {
            existing = new TikTokCredential { AppKey = _options.AppKey };
            _db.TikTokCredentials.Add(existing);
        }

        existing.EncryptedAccessToken = response.AccessToken != null ? _protector.Protect(response.AccessToken) : null;
        existing.EncryptedRefreshToken = response.RefreshToken != null ? _protector.Protect(response.RefreshToken) : null;
        existing.ExpiresAt = response.ExpiresIn.HasValue ? DateTime.UtcNow.AddSeconds(response.ExpiresIn.Value) : null;
        existing.RefreshExpiresAt = response.RefreshExpiresIn.HasValue ? DateTime.UtcNow.AddSeconds(response.RefreshExpiresIn.Value) : null;
        existing.OpenId = response.OpenId;
        existing.SellerName = response.SellerName;
        existing.Region = existing.Region ?? null;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return response;
    }

    public async Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AppKey) || string.IsNullOrWhiteSpace(_options.AppSecret))
            throw new InvalidOperationException("TikTok AppKey/AppSecret not configured. Set via user secrets or environment variables.");

        if (credential == null) return null;

        var refreshToken = credential.EncryptedRefreshToken != null ? _protector.Unprotect(credential.EncryptedRefreshToken) : null;
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
        var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

        var response = new TikTokTokenResponse
        {
            AccessToken = data.GetPropertyOrDefault("access_token"),
            RefreshToken = data.GetPropertyOrDefault("refresh_token"),
            ExpiresIn = data.GetPropertyOrDefaultInt("expires_in"),
            RefreshExpiresIn = data.GetPropertyOrDefaultInt("refresh_expires_in"),
            OpenId = data.GetPropertyOrDefault("open_id"),
            SellerName = data.GetPropertyOrDefault("seller_name")
        };

        // Update credential
        credential.EncryptedAccessToken = response.AccessToken != null ? _protector.Protect(response.AccessToken) : credential.EncryptedAccessToken;
        credential.EncryptedRefreshToken = response.RefreshToken != null ? _protector.Protect(response.RefreshToken) : credential.EncryptedRefreshToken;
        credential.ExpiresAt = response.ExpiresIn.HasValue ? DateTime.UtcNow.AddSeconds(response.ExpiresIn.Value) : credential.ExpiresAt;
        credential.RefreshExpiresAt = response.RefreshExpiresIn.HasValue ? DateTime.UtcNow.AddSeconds(response.RefreshExpiresIn.Value) : credential.RefreshExpiresAt;
        credential.OpenId = response.OpenId ?? credential.OpenId;
        credential.SellerName = response.SellerName ?? credential.SellerName;
        credential.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return response;
    }
}
