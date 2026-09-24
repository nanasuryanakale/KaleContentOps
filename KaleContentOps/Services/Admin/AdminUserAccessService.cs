using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Services.Admin;

/// <summary>Result of an admin user / role mutation.</summary>
public sealed class AdminOperationResult
{
    public bool Succeeded { get; private init; }
    public string? ErrorCode { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = Array.Empty<string>();

    public static AdminOperationResult Ok() => new() { Succeeded = true };

    public static AdminOperationResult Fail(string errorCode, params string[] errors) => new()
    {
        Succeeded = false,
        ErrorCode = errorCode,
        Errors = errors
    };

    public static AdminOperationResult FromIdentity(IEnumerable<IdentityError> identityErrors) => new()
    {
        Succeeded = false,
        ErrorCode = "IDENTITY_ERROR",
        Errors = identityErrors.Select(e => e.Description).ToArray()
    };
}

/// <summary>
/// Admin Management MVP service: Master User + Master Role &amp; Permission operations.
///
/// Rules enforced here (server-side, never only in the UI):
/// - Passwords are always set through UserManager (CreateAsync / reset-token flow) -
///   PasswordHash is never written manually.
/// - Role membership uses the existing Identity roles; permission claims stay in
///   AspNetRoleClaims (existing source of truth). Only known permission constants
///   from AuthConstants.AllKnownPermissions can be assigned - no free-text permissions.
/// - Last-admin safeguards: an operation that would leave the application with no
///   active user able to administer (no active Administrator member, or no active
///   holder of User.Manage / RolePermission.Manage) is rejected.
/// - No hard delete: users are deactivated (IsActive=false); inactive users cannot
///   log in (AccountController checks IsActive before PasswordSignInAsync).
///
/// Auditability: every mutation is logged through ILogger with the acting admin
/// (from ICurrentUser) and the affected user/role - consistent with the existing
/// logging-based audit approach (no second audit system is introduced).
/// </summary>
public sealed class AdminUserAccessService
{
    /// <summary>Permissions that must always remain with at least one active user.</summary>
    private static readonly string[] AdminCriticalPermissions =
    {
        AuthConstants.Permissions.UserManage,
        AuthConstants.Permissions.RolePermissionManage
    };

    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<AdminUserAccessService> _logger;

    public AdminUserAccessService(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        ICurrentUser currentUser,
        ILogger<AdminUserAccessService> logger)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _currentUser = currentUser;
        _logger = logger;
    }

    // ==================================================================
    // Queries
    // ==================================================================

    /// <summary>All users with their single effective role (MVP: one role per user).</summary>
    public async Task<List<AdminUserListItem>> GetUsersAsync()
    {
        var users = await _db.Users
            .OrderBy(u => u.UserName)
            .ToListAsync();

        var items = new List<AdminUserListItem>(users.Count);
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            items.Add(new AdminUserListItem
            {
                Id = user.Id,
                UserName = user.UserName ?? string.Empty,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Role = roles.FirstOrDefault() ?? string.Empty,
                Roles = roles.ToArray(),
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt,
                UpdatedAt = user.UpdatedAt,
                LastLoginAt = user.LastLoginAt
            });
        }

        return items;
    }

    public async Task<List<AdminRoleListItem>> GetRolesAsync()
    {
        var roles = await _roleManager.Roles.OrderBy(r => r.Name).ToListAsync();
        var items = new List<AdminRoleListItem>(roles.Count);
        foreach (var role in roles)
        {
            var claims = await _roleManager.GetClaimsAsync(role);
            var activeMembers = await (
                from user in _db.Users
                where user.IsActive
                join userRole in _db.UserRoles on user.Id equals userRole.UserId
                where userRole.RoleId == role.Id
                select user.Id).CountAsync();
            items.Add(new AdminRoleListItem
            {
                Id = role.Id,
                Name = role.Name ?? string.Empty,
                Description = role.Description,
                Permissions = claims
                    .Where(c => c.Type == AuthConstants.PermissionClaimType)
                    .Select(c => c.Value)
                    .Where(v => v != null)
                    .Select(v => v!)
                    .ToArray(),
                ActiveMemberCount = activeMembers
            });
        }

        return items;
    }

    /// <summary>Roles offered by the Master User form: the built-in roles that exist.</summary>
    public async Task<List<(string Id, string Name)>> GetAssignableRolesAsync()
    {
        var roles = await _roleManager.Roles.OrderBy(r => r.Name).Select(r => new { r.Id, r.Name }).ToListAsync();
        return roles.Select(r => (r.Id, r.Name ?? string.Empty)).ToList();
    }

    // ==================================================================
    // User mutations
    // ==================================================================

    public async Task<AdminOperationResult> CreateUserAsync(CreateAdminUserInput input)
    {
        var userName = input.UserName?.Trim() ?? string.Empty;
        if (userName.Length == 0)
        {
            return AdminOperationResult.Fail("VALIDATION", "Username wajib diisi.");
        }

        var roleName = input.RoleName?.Trim() ?? string.Empty;
        if (!await _roleManager.RoleExistsAsync(roleName))
        {
            return AdminOperationResult.Fail("ROLE_NOT_FOUND", $"Role '{roleName}' tidak dikenal.");
        }

        if (await _userManager.FindByNameAsync(userName) is not null)
        {
            return AdminOperationResult.Fail("DUPLICATE_USERNAME", $"Username '{userName}' sudah digunakan.");
        }

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = input.Email?.Trim(),
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? userName : input.DisplayName!.Trim(),
            IsActive = input.IsActive
        };

        // UserManager hashes the password (PBKDF2) - never stored or logged in plaintext.
        var createResult = await _userManager.CreateAsync(user, input.Password ?? string.Empty);
        if (!createResult.Succeeded)
        {
            return AdminOperationResult.FromIdentity(createResult.Errors);
        }

        var roleResult = await _userManager.AddToRoleAsync(user, roleName);
        if (!roleResult.Succeeded)
        {
            // Roll the half-created user back so a failed create never leaves a roleless user behind.
            await _userManager.DeleteAsync(user);
            return AdminOperationResult.FromIdentity(roleResult.Errors);
        }

        LogAdminAction("UserCreated", target: user.UserName, detail: $"role={roleName}, active={user.IsActive}");
        return AdminOperationResult.Ok();
    }

    public async Task<AdminOperationResult> UpdateUserAsync(string userId, UpdateAdminUserInput input)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return AdminOperationResult.Fail("USER_NOT_FOUND", "User tidak ditemukan.");
        }

        var roleName = input.RoleName?.Trim() ?? string.Empty;
        if (!await _roleManager.RoleExistsAsync(roleName))
        {
            return AdminOperationResult.Fail("ROLE_NOT_FOUND", $"Role '{roleName}' tidak dikenal.");
        }

        // ---- Last-admin guard for role change + deactivation -----------------
        var currentRoles = await _userManager.GetRolesAsync(user);
        var losesAdminRole = currentRoles.Contains(AuthConstants.Roles.Administrator)
                             && roleName != AuthConstants.Roles.Administrator;
        var losesActivity = user.IsActive && !input.IsActive;

        if ((losesAdminRole || losesActivity)
            && !await ActiveAdministratorRemainsExcludingAsync(user.Id))
        {
            return AdminOperationResult.Fail(
                "LAST_ADMIN",
                "Operasi ditolak: harus tetap ada minimal satu user Administrator aktif.");
        }

        if ((losesAdminRole || losesActivity)
            && !await ActivePermissionHolderRemainsExcludingAsync(AuthConstants.Permissions.UserManage, user.Id))
        {
            return AdminOperationResult.Fail(
                "LAST_ADMIN",
                "Operasi ditolak: harus tetap ada minimal satu user aktif dengan permission User.Manage.");
        }

        // ---- Profile fields ---------------------------------------------------
        user.DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? user.DisplayName : input.DisplayName!.Trim();
        user.Email = input.Email?.Trim();
        user.IsActive = input.IsActive;
        user.UpdatedAt = DateTime.UtcNow;
        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return AdminOperationResult.FromIdentity(updateResult.Errors);
        }

        // ---- Role membership (MVP: exactly one role) --------------------------
        var rolesToRemove = currentRoles.Where(r => r != roleName).ToArray();
        foreach (var role in rolesToRemove)
        {
            var removeResult = await _userManager.RemoveFromRoleAsync(user, role);
            if (!removeResult.Succeeded)
            {
                return AdminOperationResult.FromIdentity(removeResult.Errors);
            }
        }

        if (!currentRoles.Contains(roleName))
        {
            var addResult = await _userManager.AddToRoleAsync(user, roleName);
            if (!addResult.Succeeded)
            {
                return AdminOperationResult.FromIdentity(addResult.Errors);
            }
        }

        LogAdminAction("UserUpdated", target: user.UserName,
            detail: $"role={roleName}, active={user.IsActive}, displayName={user.DisplayName}");
        return AdminOperationResult.Ok();
    }

    public async Task<AdminOperationResult> SetUserActiveAsync(string userId, bool isActive)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return AdminOperationResult.Fail("USER_NOT_FOUND", "User tidak ditemukan.");
        }

        if (user.IsActive && !isActive
            && !await ActiveAdministratorRemainsExcludingAsync(user.Id))
        {
            return AdminOperationResult.Fail(
                "LAST_ADMIN",
                "Operasi ditolak: tidak boleh menonaktifkan satu-satunya Administrator aktif.");
        }

        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return AdminOperationResult.FromIdentity(result.Errors);
        }

        // Inactive users cannot log in (AccountController blocks IsActive=false before
        // PasswordSignInAsync). Existing cookies keep working until expiry/re-auth,
        // matching the documented behaviour of ApplicationUser.IsActive.
        LogAdminAction(isActive ? "UserActivated" : "UserDeactivated", target: user.UserName ?? userId);
        return AdminOperationResult.Ok();
    }

    public async Task<AdminOperationResult> SetPasswordAsync(string userId, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return AdminOperationResult.Fail("USER_NOT_FOUND", "User tidak ditemukan.");
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            return AdminOperationResult.Fail("VALIDATION", "Password baru wajib diisi.");
        }

        // Identity-supported reset flow: token is generated server-side, the new
        // password is validated against the password policy and hashed by UserManager.
        // Never reads/echoes/displays the existing password.
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            return AdminOperationResult.FromIdentity(result.Errors);
        }

        LogAdminAction("UserPasswordReset", target: user.UserName ?? userId);
        return AdminOperationResult.Ok();
    }

    // ==================================================================
    // Role & permission mutations
    // ==================================================================

    public async Task<AdminOperationResult> SetRolePermissionsAsync(string roleId, IEnumerable<string> requestedPermissions)
    {
        var role = await _roleManager.FindByIdAsync(roleId);
        if (role is null || string.IsNullOrEmpty(role.Name))
        {
            return AdminOperationResult.Fail("ROLE_NOT_FOUND", "Role tidak ditemukan.");
        }

        var requested = requestedPermissions.Distinct().ToArray();

        // Permission selection must come from the known registry - never free text.
        // Unknown values would produce claims that no policy can use (deny by default).
        var unknown = requested.Where(p => !AuthConstants.AllKnownPermissions.Contains(p)).ToArray();
        if (unknown.Length > 0)
        {
            return AdminOperationResult.Fail("UNKNOWN_PERMISSION",
                $"Permission tidak dikenal: {string.Join(", ", unknown)}");
        }

        var currentClaims = await _roleManager.GetClaimsAsync(role);
        var current = currentClaims
            .Where(c => c.Type == AuthConstants.PermissionClaimType)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        var toAdd = requested.Where(p => !current.Contains(p)).ToArray();
        // Only KNOWN permissions are ever removed. Claims that predate the registry
        // (manually inserted legacy values) are preserved untouched - removing them
        // through this UI could silently break custom grants.
        var toRemove = current
            .Where(p => !requested.Contains(p) && AuthConstants.AllKnownPermissions.Contains(p))
            .ToArray();
        var preservedUnknown = current
            .Where(p => !AuthConstants.AllKnownPermissions.Contains(p))
            .ToArray();

        // ---- Last-admin guard (simulated post-state) --------------------------
        // Users holding a critical permission only via THIS role would lose it when the
        // claim is removed; holders via other roles are unaffected.
        foreach (var permission in AdminCriticalPermissions)
        {
            if (toRemove.Contains(permission) && !requested.Contains(permission))
            {
                if (!await ActivePermissionHolderRemainsOutsideRoleAsync(permission, role.Id))
                {
                    return AdminOperationResult.Fail(
                        "LAST_ADMIN",
                        $"Operasi ditolak: menghapus '{permission}' dari role '{role.Name}' akan meninggalkan " +
                        "tanpa satu pun user aktif yang memiliki permission tersebut.");
                }
            }
        }

        // ---- Apply (only the delta, idempotent) --------------------------------
        foreach (var permission in toAdd)
        {
            var addResult = await _roleManager.AddClaimAsync(role,
                new System.Security.Claims.Claim(AuthConstants.PermissionClaimType, permission));
            if (!addResult.Succeeded)
            {
                return AdminOperationResult.FromIdentity(addResult.Errors);
            }
        }

        foreach (var permission in toRemove)
        {
            var claim = currentClaims.First(c =>
                c.Type == AuthConstants.PermissionClaimType && c.Value == permission);
            var removeResult = await _roleManager.RemoveClaimAsync(role, claim);
            if (!removeResult.Succeeded)
            {
                return AdminOperationResult.FromIdentity(removeResult.Errors);
            }
        }

        LogAdminAction("RolePermissionsUpdated", target: role.Name,
            detail: $"added=[{string.Join(",", toAdd)}] removed=[{string.Join(",", toRemove)}]" +
                    (preservedUnknown.Length > 0 ? $" preservedUnknown=[{string.Join(",", preservedUnknown)}]" : string.Empty));
        return AdminOperationResult.Ok();
    }

    // ==================================================================
    // Safeguard helpers
    // ==================================================================

    /// <summary>True when at least one ACTIVE user other than excludedUserId is an Administrator member.</summary>
    private async Task<bool> ActiveAdministratorRemainsExcludingAsync(string? excludedUserId)
    {
        var adminRole = await _roleManager.FindByNameAsync(AuthConstants.Roles.Administrator);
        if (adminRole is null)
        {
            return false;
        }

        var query = from user in _db.Users
                    where user.IsActive
                    join userRole in _db.UserRoles on user.Id equals userRole.UserId
                    where userRole.RoleId == adminRole.Id
                    select user.Id;
        if (excludedUserId is not null)
        {
            query = query.Where(id => id != excludedUserId);
        }

        return await query.AnyAsync();
    }

    /// <summary>
    /// True when at least one ACTIVE user other than excludedUserId holds the given
    /// permission through any of their roles' permission claims.
    /// </summary>
    private async Task<bool> ActivePermissionHolderRemainsExcludingAsync(string permission, string? excludedUserId)
    {
        var query = from user in _db.Users
                    where user.IsActive
                    join userRole in _db.UserRoles on user.Id equals userRole.UserId
                    join roleClaim in _db.RoleClaims on userRole.RoleId equals roleClaim.RoleId
                    where roleClaim.ClaimType == AuthConstants.PermissionClaimType
                          && roleClaim.ClaimValue == permission
                    select user.Id;

        if (excludedUserId is not null)
        {
            query = query.Where(id => id != excludedUserId);
        }

        return await query.Distinct().AnyAsync();
    }

    /// <summary>
    /// True when at least one ACTIVE user holds the given permission WITHOUT relying on
    /// the given role (used before removing a claim from a role).
    /// </summary>
    private async Task<bool> ActivePermissionHolderRemainsOutsideRoleAsync(string permission, string roleId)
    {
        var query = from user in _db.Users
                    where user.IsActive
                    join userRole in _db.UserRoles on user.Id equals userRole.UserId
                    join roleClaim in _db.RoleClaims on userRole.RoleId equals roleClaim.RoleId
                    where roleClaim.ClaimType == AuthConstants.PermissionClaimType
                          && roleClaim.ClaimValue == permission
                          && userRole.RoleId != roleId
                    select user.Id;

        return await query.Distinct().AnyAsync();
    }

    // ==================================================================
    // Audit logging (existing ILogger-based mechanism)
    // ==================================================================

    private void LogAdminAction(string action, string target, string? detail = null)
    {
        _logger.LogWarning(
            "ADMIN_AUDIT {AdminAction} by {ActorUserName} ({ActorUserId}) on {Target} {Detail}",
            action, _currentUser.UserName ?? "?", _currentUser.UserId ?? "?", target, detail ?? string.Empty);
    }
}

// ======================================================================
// Inputs / list models
// ======================================================================

public sealed record CreateAdminUserInput
{
    public string UserName { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string RoleName { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
}

public sealed record UpdateAdminUserInput
{
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public string RoleName { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
}

public sealed class AdminUserListItem
{
    public string Id { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Role { get; set; } = string.Empty;
    public string[] Roles { get; set; } = Array.Empty<string>();
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public sealed class AdminRoleListItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string[] Permissions { get; set; } = Array.Empty<string>();
    public int ActiveMemberCount { get; set; }
}
