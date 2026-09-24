using KaleContentOps.Models;
using KaleContentOps.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace KaleContentOps.Data;

/// <summary>
/// Idempotent bootstrap for the Identity foundation:
/// - creates built-in roles (Administrator / Manager / Viewer) if missing,
/// - ensures every built-in role carries its default permission claims,
/// - creates the first Administrator from configuration if no admin exists.
/// Safe to run on every startup: existing users/roles/claims are left untouched
/// and no duplicate rows are created. Passwords are always hashed by UserManager.
/// </summary>
public static class AdminSeeder
{
    /// <summary>
    /// Runs the seeder. Returns a summary of what was done (for logging/diagnostics).
    /// Never throws for "already exists" situations - those are no-ops.
    /// </summary>
    public static async Task<AdminSeedResult> SeedAsync(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        AdminSeedOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = new AdminSeedResult();

        if (options is null || !options.Enabled)
        {
            result.Disabled = true;
            return result;
        }

        // --------------------------------------------------------------
        // 1. Roles
        // --------------------------------------------------------------
        // Full known set (Target.*, User.*, RolePermission.*) - Administrator always gets
        // everything so newly introduced permissions are picked up on reseed automatically.
        var allPermissions = AuthConstants.AllKnownPermissions.ToArray();

        var adminPermissions = allPermissions; // Administrator always gets the full set

        foreach (var roleName in new[] { AuthConstants.Roles.Administrator, AuthConstants.Roles.Manager, AuthConstants.Roles.Viewer })
        {
            if (await roleManager.RoleExistsAsync(roleName))
            {
                result.RolesAlreadyPresent++;
            }
            else
            {
                var createResult = await roleManager.CreateAsync(new ApplicationRole(roleName)
                {
                    Description = $"Built-in {roleName} role (created by startup seeder)."
                });
                if (createResult.Succeeded)
                {
                    result.RolesCreated++;
                }
                else
                {
                    result.Errors.AddRange(createResult.Errors.Select(e => $"{roleName}: [{e.Code}] {e.Description}"));
                    continue;
                }
            }

            // Ensure permission claims on the role (idempotent).
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                continue;
            }

            var existingClaims = await roleManager.GetClaimsAsync(role);
            var existingPermissionValues = existingClaims
                .Where(c => c.Type == AuthConstants.PermissionClaimType)
                .Select(c => c.Value)
                .ToHashSet(StringComparer.Ordinal);

            var desiredPermissions = roleName == AuthConstants.Roles.Administrator
                ? adminPermissions
                : AuthConstants.DefaultRolePermissions.TryGetValue(roleName, out var perms)
                    ? perms
                    : Array.Empty<string>();

            foreach (var permission in desiredPermissions)
            {
                if (!existingPermissionValues.Contains(permission))
                {
                    var claimResult = await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim(AuthConstants.PermissionClaimType, permission));
                    if (claimResult.Succeeded)
                    {
                        result.RoleClaimsAdded++;
                    }
                    else
                    {
                        result.Errors.AddRange(claimResult.Errors.Select(e => $"{roleName}/{permission}: [{e.Code}] {e.Description}"));
                    }
                }
            }
        }

        // --------------------------------------------------------------
        // 2. First administrator (only when none exists)
        // --------------------------------------------------------------
        var adminsInRole = await userManager.GetUsersInRoleAsync(AuthConstants.Roles.Administrator);
        if (adminsInRole.Count > 0)
        {
            result.AdminAlreadyExists = true;
            return result;
        }

        var (adminUserName, adminEmail, adminDisplayName, adminPassword) = ResolveAdminCredentials(options);
        if (adminUserName is null || adminPassword is null)
        {
            result.Errors.Add(
                "No administrator exists yet, but InitialAdmin credentials are incomplete. " +
                "Set InitialAdmin:Username, InitialAdmin:Email and InitialAdmin:Password (user secrets in Development, environment variables in production).");
            return result;
        }

        var admin = new ApplicationUser
        {
            UserName = adminUserName,
            Email = adminEmail,
            EmailConfirmed = true,
            DisplayName = adminDisplayName ?? adminUserName,
            IsActive = true
        };

        var userResult = await userManager.CreateAsync(admin, adminPassword);
        if (!userResult.Succeeded)
        {
            result.Errors.AddRange(userResult.Errors.Select(e => $"Initial admin '{adminUserName}': [{e.Code}] {e.Description}"));
            return result;
        }

        result.AdminCreated = true;

        var roleAddResult = await userManager.AddToRoleAsync(admin, AuthConstants.Roles.Administrator);
        if (!roleAddResult.Succeeded)
        {
            result.Errors.AddRange(roleAddResult.Errors.Select(e => $"Initial admin '{adminUserName}': [{e.Code}] {e.Description}"));
        }

        return result;
    }

    private static (string? UserName, string? Email, string? DisplayName, string? Password) ResolveAdminCredentials(
        AdminSeedOptions options)
    {
        var userName = options.Username;
        var password = options.Password;

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return (null, null, null, null);
        }

        // Email defaults to <username>@localhost when not explicitly provided.
        var email = string.IsNullOrWhiteSpace(options.Email)
            ? $"{userName}@localhost"
            : options.Email;

        var displayName = string.IsNullOrWhiteSpace(options.DisplayName)
            ? userName
            : options.DisplayName;

        return (userName, email, displayName, password);
    }
}

/// <summary>Bound from configuration section "InitialAdmin" (never committed to source control).</summary>
public sealed class AdminSeedOptions
{
    public const string SectionName = "InitialAdmin";

    /// <summary>Set false to skip seeding entirely (e.g. unit tests, multi-instance rollouts).</summary>
    public bool Enabled { get; set; } = true;

    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
}

/// <summary>Summary of one seeder run (used for logging and tests).</summary>
public sealed class AdminSeedResult
{
    public int RolesCreated { get; set; }
    public int RolesAlreadyPresent { get; set; }
    public int RoleClaimsAdded { get; set; }
    public bool AdminCreated { get; set; }
    public bool AdminAlreadyExists { get; set; }
    public bool Disabled { get; set; }
    public List<string> Errors { get; } = new();
}
