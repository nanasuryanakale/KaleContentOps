namespace KaleContentOps.Security;

/// <summary>
/// Application-wide constants for authorization.
/// </summary>
public static class AuthConstants
{
    /// <summary>Claim type used for permissions (role claims + user claims).</summary>
    public const string PermissionClaimType = "permission";

    /// <summary>Claim type carrying the user's display name for UI purposes.</summary>
    public const string DisplayNameClaimType = "display_name";

    // ------------------------------------------------------------------
    // Known permissions (MVP). New permissions can be added here and
    // seeded onto roles - no schema change required.
    // ------------------------------------------------------------------
    public static class Permissions
    {
        public const string TargetView = "Target.View";
        public const string TargetEdit = "Target.Edit";
        public const string TargetHistoryView = "Target.History.View";

        // Admin Management MVP: Master User / Master Role & Permission.
        // View-only permission lets managers inspect users without mutating them.
        public const string UserView = "User.View";
        public const string UserManage = "User.Manage";
        public const string RolePermissionManage = "RolePermission.Manage";

        // Admin TikTok (existing screen, now grouped under the ADMIN sidebar section).
        // Introduced so the section is genuinely admin-only; previously every
        // authenticated user could open /Admin/TikTok (fallback policy only).
        public const string TikTokAdminView = "TikTokAdmin.View";
    }

    /// <summary>
    /// The full set of known permission values. Single source of truth used by:
    /// - AdminSeeder (Administrator always receives all of them),
    /// - the Role &amp; Permission UI (permission checkboxes come from here only,
    ///   never free-text input),
    /// - validation when a permission is assigned to a role.
    /// </summary>
    public static IReadOnlyList<string> AllKnownPermissions => new[]
    {
        Permissions.TargetView,
        Permissions.TargetEdit,
        Permissions.TargetHistoryView,
        Permissions.UserView,
        Permissions.UserManage,
        Permissions.RolePermissionManage,
        Permissions.TikTokAdminView
    };

    // ------------------------------------------------------------------
    // Built-in role names (MVP).
    // ------------------------------------------------------------------
    public static class Roles
    {
        public const string Administrator = "Administrator";
        public const string Manager = "Manager";
        public const string Viewer = "Viewer";
    }

    /// <summary>
    /// Default role -> permission matrix applied by the seeder for built-in roles.
    /// Administrator deliberately has NO entry: it always receives the full claim
    /// set (all known permissions) so newly introduced permissions are picked up
    /// automatically on reseed without manual mapping maintenance.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> DefaultRolePermissions => new Dictionary<string, string[]>
    {
        [Roles.Manager] = new[]
        {
            Permissions.TargetView,
            Permissions.TargetEdit,
            Permissions.TargetHistoryView
        },
        [Roles.Viewer] = new[]
        {
            Permissions.TargetView,
            Permissions.TargetHistoryView
        }
        // Note: User.View / User.Manage / RolePermission.Manage / TikTokAdmin.View are
        // intentionally NOT seeded onto Manager/Viewer - the ADMIN sidebar section and
        // its screens are visible to Administrators only (all known permissions are
        // granted to Administrator automatically).
    };
}
