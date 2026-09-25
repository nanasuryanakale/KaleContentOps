using Microsoft.AspNetCore.Identity;

namespace KaleContentOps.Models;

/// <summary>
/// Application role (Administrator / Manager / Viewer for MVP).
/// Permissions are attached to roles as role claims ("permission" claim type),
/// so new permissions can be introduced without schema changes.
/// </summary>
public class ApplicationRole : IdentityRole
{
    public ApplicationRole() { }

    public ApplicationRole(string roleName) : base(roleName) { }

    // Optional free-text description of what the role is for.
    public string? Description { get; set; }
}
