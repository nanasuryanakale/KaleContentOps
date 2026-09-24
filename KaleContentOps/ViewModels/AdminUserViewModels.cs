using System;
using System.Collections.Generic;
using KaleContentOps.Services.Admin;

namespace KaleContentOps.ViewModels;

/// <summary>Master User page: user list + roles offered by the create/edit form.</summary>
public sealed class MasterUserPageViewModel
{
    public List<AdminUserListItem> Users { get; set; } = new();

    /// <summary>Existing Identity roles the form may assign (Administrator / Manager / Viewer).</summary>
    public List<AssignableRoleOption> AssignableRoles { get; set; } = new();
}

public sealed class AssignableRoleOption
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>Master Role &amp; Permission page: roles with their claims + the known permission registry.</summary>
public sealed class MasterRolesPageViewModel
{
    public List<AdminRoleListItem> Roles { get; set; } = new();

    /// <summary>All known permissions (AuthConstants registry) renderable as checkboxes.</summary>
    public List<string> KnownPermissions { get; set; } = new();
}

// ----------------------------------------------------------------------
// JSON request payloads (mutations are POST + JSON + antiforgery header,
// following the existing /targets/save client pattern).
// ----------------------------------------------------------------------

public sealed class AdminUpdateUserRequest
{
    public string UserId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class AdminSetActiveRequest
{
    public string UserId { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class AdminSetPasswordRequest
{
    public string UserId { get; set; } = string.Empty;
    public string? NewPassword { get; set; }
}

public sealed class AdminSetRolePermissionsRequest
{
    public string RoleId { get; set; } = string.Empty;
    public string[] Permissions { get; set; } = Array.Empty<string>();
}
