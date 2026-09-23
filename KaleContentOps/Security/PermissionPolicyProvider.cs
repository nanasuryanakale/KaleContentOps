using KaleContentOps.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace KaleContentOps.Security;

/// <summary>
/// Resolves policies named "Permission:Target.Edit" etc. at runtime.
/// The policy passes when the user holds the permission via ANY claim
/// (role claim or user claim) - roles remain management units, but the
/// code authorizes against permissions, not hard-coded role names.
/// Unknown permissions resolve to a never-succeeding policy (deny by default).
/// </summary>
public sealed class PermissionPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public const string PolicyPrefix = "Permission:";

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        : base(options)
    {
    }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
        {
            var permission = policyName.Substring(PolicyPrefix.Length);

            if (string.IsNullOrWhiteSpace(permission))
            {
                return DenyAll();
            }

            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim(AuthConstants.PermissionClaimType, permission)
                .Build();
        }

        return await base.GetPolicyAsync(policyName);
    }

    private static AuthorizationPolicy DenyAll() =>
        new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => false)
            .Build();
}
