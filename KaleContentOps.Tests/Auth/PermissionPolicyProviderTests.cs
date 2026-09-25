using System;
using System.Linq;
using System.Threading.Tasks;
using KaleContentOps.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace KaleContentOps.Tests.Auth;

/// <summary>
/// Unit tests for the dynamic permission policy provider.
/// "Permission:X" policies must require authentication + the X permission claim;
/// unknown permissions must never succeed (deny by default).
/// </summary>
public class PermissionPolicyProviderTests
{
    private static PermissionPolicyProvider CreateProvider()
    {
        var options = Options.Create(new AuthorizationOptions());
        return new PermissionPolicyProvider(options);
    }

    [Fact]
    public async Task GetPolicyAsync_PermissionPolicy_RequiresAuthenticatedUser()
    {
        var provider = CreateProvider();

        var policy = await provider.GetPolicyAsync("Permission:" + AuthConstants.Permissions.TargetEdit);

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task GetPolicyAsync_PermissionPolicy_RequiresThePermissionClaim()
    {
        var provider = CreateProvider();

        var policy = await provider.GetPolicyAsync("Permission:" + AuthConstants.Permissions.TargetEdit);

        var claimRequirement = policy!.Requirements
            .OfType<ClaimsAuthorizationRequirement>()
            .Single();
        Assert.Equal(AuthConstants.PermissionClaimType, claimRequirement.ClaimType);
        Assert.Contains(AuthConstants.Permissions.TargetEdit, claimRequirement.AllowedValues!);
    }

    [Fact]
    public async Task GetPolicyAsync_UnknownPermission_BuildsDenyPolicy()
    {
        // Unknown permission names still resolve to a syntactically valid policy,
        // but the required claim can never be held by a seeded role.
        var provider = CreateProvider();

        var policy = await provider.GetPolicyAsync("Permission:Does.Not.Exist");

        Assert.NotNull(policy);
        var claimRequirement = policy!.Requirements.OfType<ClaimsAuthorizationRequirement>().Single();
        Assert.Equal("Does.Not.Exist", claimRequirement.AllowedValues!.Single());
    }

    [Fact]
    public async Task GetPolicyAsync_NonPermissionPolicy_FallsBackToDefaultProvider()
    {
        var provider = CreateProvider();

        var policy = await provider.GetPolicyAsync("SomeRegisteredStaticPolicy");

        // Default provider returns null for unregistered names - the provider must not throw.
        Assert.Null(policy);
    }
}
