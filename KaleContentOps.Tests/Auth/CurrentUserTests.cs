using System.Security.Claims;
using KaleContentOps.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace KaleContentOps.Tests.Auth;

/// <summary>
/// CurrentUser resolution from the application layer:
/// - authenticated principal -> stable NameIdentifier as UserId,
/// - anonymous principal -> null UserId / IsAuthenticated=false,
/// - permission checks read the "permission" claim.
/// This is the exact API Target History will use for ChangedByUserId.
/// </summary>
public class CurrentUserTests
{
    private static CurrentUser Create(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        return new CurrentUser(new HttpContextAccessor { HttpContext = httpContext });
    }

    [Fact]
    public void AuthenticatedPrincipal_ExposesStableUserId()
    {
        var user = Create(
            new Claim(ClaimTypes.NameIdentifier, "6f1c2b3a-1111-4a4a-9c9c-abcdefabcdef"),
            new Claim(ClaimTypes.Name, "budi"),
            new Claim(AuthConstants.PermissionClaimType, AuthConstants.Permissions.TargetEdit));

        Assert.True(user.IsAuthenticated);
        Assert.Equal("6f1c2b3a-1111-4a4a-9c9c-abcdefabcdef", user.UserId);
        Assert.Equal("budi", user.UserName);
    }

    [Fact]
    public void AnonymousPrincipal_HasNullUserId()
    {
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var user = new CurrentUser(new HttpContextAccessor { HttpContext = httpContext });

        Assert.False(user.IsAuthenticated);
        Assert.Null(user.UserId);
        Assert.Null(user.UserName);
    }

    [Fact]
    public void DisplayName_FallsBackToUserNameWhenClaimMissing()
    {
        var user = Create(new Claim(ClaimTypes.Name, "sari"));

        Assert.Equal("sari", user.DisplayName);
    }

    [Fact]
    public void DisplayName_PrefersDisplayNameClaim()
    {
        var user = Create(
            new Claim(ClaimTypes.Name, "sari"),
            new Claim(AuthConstants.DisplayNameClaimType, "Sari Wulandari"));

        Assert.Equal("Sari Wulandari", user.DisplayName);
    }

    [Fact]
    public void HasPermission_MatchesPermissionClaimExactValue()
    {
        var user = Create(new Claim(AuthConstants.PermissionClaimType, AuthConstants.Permissions.TargetView));

        Assert.True(user.HasPermission(AuthConstants.Permissions.TargetView));
        Assert.False(user.HasPermission(AuthConstants.Permissions.TargetEdit));
        Assert.False(user.HasPermission(AuthConstants.Permissions.TargetHistoryView));
    }
}
