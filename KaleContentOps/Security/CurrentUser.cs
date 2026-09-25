using System.Security.Claims;

namespace KaleContentOps.Security;

/// <summary>
/// Abstraction over the current authenticated user, so business logic
/// (e.g. future Target History ChangedByUserId) never depends on
/// HttpContext or Identity internals directly.
/// The Id is the stable ASP.NET Core Identity user Id (GUID string).
/// </summary>
public interface ICurrentUser
{
    /// <summary>Stable Identity user Id, or null for anonymous requests.</summary>
    string? UserId { get; }

    /// <summary>UserName (login name), or null for anonymous requests.</summary>
    string? UserName { get; }

    /// <summary>Display name for UI purposes, falling back to UserName.</summary>
    string? DisplayName { get; }

    /// <summary>True when the request is authenticated.</summary>
    bool IsAuthenticated { get; }

    /// <summary>True when the user holds the given permission claim.</summary>
    bool HasPermission(string permission);
}

/// <summary>
/// HttpContext-backed implementation. Registered as Scoped; resolved per request.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal =>
        _httpContextAccessor.HttpContext?.User;

    public string? UserId =>
        Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

    public string? UserName =>
        Principal?.FindFirstValue(ClaimTypes.Name);

    public string? DisplayName
    {
        get
        {
            var principal = Principal;
            if (principal is null)
            {
                return null;
            }

            var display = principal.FindFirstValue(AuthConstants.DisplayNameClaimType);
            return string.IsNullOrWhiteSpace(display)
                ? principal.FindFirstValue(ClaimTypes.Name)
                : display;
        }
    }

    public bool IsAuthenticated =>
        Principal?.Identity?.IsAuthenticated == true;

    public bool HasPermission(string permission) =>
        Principal?.HasClaim(AuthConstants.PermissionClaimType, permission) == true;
}
