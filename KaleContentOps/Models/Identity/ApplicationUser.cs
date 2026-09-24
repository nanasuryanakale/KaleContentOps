using Microsoft.AspNetCore.Identity;

namespace KaleContentOps.Models;

/// <summary>
/// Application user for Kale Content Ops (ASP.NET Core Identity).
/// Identity supplies the stable Id (GUID string), UserName, Email, password hash,
/// lockout and security stamp; the app-specific columns are the ones below.
/// IsActive=false blocks new logins; existing sessions keep working until the
/// cookie expires (re-authentication is rejected).
/// </summary>
public class ApplicationUser : IdentityUser
{
    // Human-friendly name shown in the UI (topbar, future Target History).
    public string DisplayName { get; set; } = string.Empty;

    // Inactive users cannot log in.
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Admin Management MVP: last successful login, stamped by the login flow via
    // UserManager (never written by user input). Nullable - null means never logged in.
    public DateTime? LastLoginAt { get; set; }
}
