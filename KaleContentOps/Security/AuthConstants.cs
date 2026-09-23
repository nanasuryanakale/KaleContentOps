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
    }

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
    };
}
