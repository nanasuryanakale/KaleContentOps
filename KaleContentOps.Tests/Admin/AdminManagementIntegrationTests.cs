using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.Admin;

/// <summary>
/// Admin Management MVP - Master User + Master Role &amp; Permission integration tests
/// over the real pipeline (Identity, cookie auth, permission policies, antiforgery).
/// Covers: create/list/edit users, Identity password hashing, login of created users,
/// deactivation blocking logins, admin password reset, duplicate username + weak
/// password rejection, role assignment effect on authorization, permission claim
/// management on AspNetRoleClaims, last-admin safeguards, and direct-URL protection.
/// </summary>
public class AdminUserManagementIntegrationTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;
    private const string AdminPassword = "Admin-Passw0rd!X";

    public AdminUserManagementIntegrationTests(AuthTestFactory factory)
    {
        _factory = factory;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<(HttpClient Client, string Token)> SignInAdminAsync(string username)
    {
        await _factory.CreateRoleUserAsync(username, AuthConstants.Roles.Administrator, AdminPassword);
        var client = await _factory.SignInAsync(username, AdminPassword, allowAutoRedirect: true);
        var page = await client.GetAsync("/Admin/Users");
        if (!page.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET /Admin/Users -> {(int)page.StatusCode}: {await page.Content.ReadAsStringAsync()}");
        }
        return (client, AuthTestFactory.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync()) ?? string.Empty);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object payload, string token)
    {
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        var response = await client.PostAsync(url,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement;
    }

    private async Task<ApplicationUser?> FindUserAsync(string username)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync(username);
        if (user is null) return null;
        user.PasswordHash = null; // avoid carrying the hash out of the scope
        return user;
    }

    // ------------------------------------------------------------------
    // 1. Admin can list users
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_CanListUsers()
    {
        await _factory.CreateRoleUserAsync("list.target1", AuthConstants.Roles.Viewer, AdminPassword);
        var (client, _) = await SignInAdminAsync("list.admin");

        var page = await client.GetAsync("/Admin/Users");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("list.target1", html);
        Assert.Contains("Master User", html);
    }

    // ------------------------------------------------------------------
    // 2/3. Create user + password hashed through Identity
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_CreateUser_HashesPasswordThroughIdentity()
    {
        var (client, token) = await SignInAdminAsync("create.admin");

        var result = await PostJsonAsync(client, "/Admin/Users/Create", new
        {
            userName = "created.user1",
            displayName = "Created User",
            email = "created.user1@kale.local",
            roleName = AuthConstants.Roles.Viewer,
            password = "Strong-Passw0rd!1",
            isActive = true
        }, token);

        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync("created.user1");
        Assert.NotNull(user);
        // PasswordHash comes from the Identity PBKDF2 formatter - never the plaintext.
        Assert.NotEqual("Strong-Passw0rd!1", user!.PasswordHash);
        Assert.StartsWith("AQAAAA", user.PasswordHash);
        Assert.True(user.IsActive);

        // 12. User receives the assigned role.
        Assert.True(await userManager.IsInRoleAsync(user, AuthConstants.Roles.Viewer));
    }

    // ------------------------------------------------------------------
    // 4. Created user can login
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreatedUser_CanLogin()
    {
        var (client, token) = await SignInAdminAsync("login.admin");
        var createResult = await PostJsonAsync(client, "/Admin/Users/Create", new
        {
            userName = "login.target1",
            roleName = AuthConstants.Roles.Viewer,
            password = "Another-Passw0rd!2",
            isActive = true
        }, token);
        Assert.True(createResult.GetProperty("success").GetBoolean(), createResult.GetRawText());

        // Real login through the cookie pipeline (SignInAsync asserts the 302 on success).
        await _factory.SignInAsync("login.target1", "Another-Passw0rd!2");
    }

    // ------------------------------------------------------------------
    // 5. Admin can edit user
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_CanEditUser()
    {
        var (client, token) = await SignInAdminAsync("edit.admin");
        var userId = await _factory.CreateRoleUserAsync("edit.target1", AuthConstants.Roles.Viewer, AdminPassword);

        var result = await PostJsonAsync(client, "/Admin/Users/Update", new
        {
            userId,
            displayName = "Edited Name",
            email = "edited@kale.local",
            roleName = AuthConstants.Roles.Viewer,
            isActive = true
        }, token);

        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        var user = await FindUserAsync("edit.target1");
        Assert.NotNull(user);
        Assert.Equal("Edited Name", user!.DisplayName);
        Assert.Equal("edited@kale.local", user.Email);
    }

    // ------------------------------------------------------------------
    // 6/7. Deactivate user + inactive user cannot login
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_DeactivateUser_BlocksLogin()
    {
        var (client, token) = await SignInAdminAsync("deact.admin");
        var userId = await _factory.CreateRoleUserAsync("deact.target1", AuthConstants.Roles.Viewer, AdminPassword);

        var result = await PostJsonAsync(client, "/Admin/Users/SetActive", new { userId, isActive = false }, token);
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());

        var user = await FindUserAsync("deact.target1");
        Assert.NotNull(user);
        Assert.False(user!.IsActive);

        // Inactive users cannot log in (AccountController rejects before PasswordSignInAsync).
        var loginClient = _factory.CreateBrowserClient(allowAutoRedirect: false);
        var loginPage = await loginClient.GetAsync("/Account/Login");
        var loginToken = AuthTestFactory.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());
        var loginResponse = await loginClient.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "deact.target1",
            ["Password"] = AdminPassword,
            ["__RequestVerificationToken"] = loginToken ?? string.Empty
        }));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode); // form redisplayed
        Assert.Contains("tidak aktif", await loginResponse.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------
    // 8. Admin can reset/set user password
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_CanResetUserPassword()
    {
        var (client, token) = await SignInAdminAsync("reset.admin");
        var userId = await _factory.CreateRoleUserAsync("reset.target1", AuthConstants.Roles.Viewer, AdminPassword);

        var result = await PostJsonAsync(client, "/Admin/Users/SetPassword", new
        {
            userId,
            newPassword = "Brand-New-Passw0rd!3"
        }, token);
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());

        // New password works, old password no longer does.
        await _factory.SignInAsync("reset.target1", "Brand-New-Passw0rd!3");
        var oldLoginClient = _factory.CreateBrowserClient(allowAutoRedirect: false);
        var loginPage = await oldLoginClient.GetAsync("/Account/Login");
        var loginToken = AuthTestFactory.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());
        var oldLogin = await oldLoginClient.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "reset.target1",
            ["Password"] = AdminPassword,
            ["__RequestVerificationToken"] = loginToken ?? string.Empty
        }));
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode); // rejected, form redisplayed
    }

    // ------------------------------------------------------------------
    // 9/10. Duplicate username + weak password rejected
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_CreateUser_DuplicateUsername_Rejected()
    {
        var (client, token) = await SignInAdminAsync("dup.admin");
        await _factory.CreateRoleUserAsync("dup.existing", AuthConstants.Roles.Viewer, AdminPassword);

        var result = await PostJsonAsync(client, "/Admin/Users/Create", new
        {
            userName = "dup.existing",
            roleName = AuthConstants.Roles.Viewer,
            password = "Strong-Passw0rd!9",
            isActive = true
        }, token);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("DUPLICATE_USERNAME", result.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Admin_CreateUser_WeakPassword_RejectedByPolicy()
    {
        var (client, token) = await SignInAdminAsync("weak.admin");

        var result = await PostJsonAsync(client, "/Admin/Users/Create", new
        {
            userName = "weak.target1",
            roleName = AuthConstants.Roles.Viewer,
            password = "short",
            isActive = true
        }, token);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("IDENTITY_ERROR", result.GetProperty("errorCode").GetString());
        Assert.True(result.GetProperty("errors").GetArrayLength() > 0);

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await userManager.FindByNameAsync("weak.target1")); // nothing created
    }

    // ------------------------------------------------------------------
    // 11/13. Role assignment + existing authorization works after it
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_RoleAssignment_ChangesEffectiveAuthorization()
    {
        var (client, token) = await SignInAdminAsync("promote.admin");
        var userId = await _factory.CreateRoleUserAsync("promote.target1", AuthConstants.Roles.Viewer, AdminPassword);
        // Materialize the Manager role in this factory's store (roles only exist once created).
        await _factory.CreateRoleUserAsync("promote.manager.ref", AuthConstants.Roles.Manager, AdminPassword);

        // Viewer (no Target.Edit): POST /targets/save must be denied server-side (403).
        var viewerClient = await _factory.SignInAsync("promote.target1", AdminPassword);
        var targetsPage = await viewerClient.GetAsync("/Targets");
        Assert.Equal(HttpStatusCode.OK, targetsPage.StatusCode);
        var saveToken = AuthTestFactory.ExtractAntiforgeryToken(await targetsPage.Content.ReadAsStringAsync());
        viewerClient.DefaultRequestHeaders.Remove("RequestVerificationToken");
        viewerClient.DefaultRequestHeaders.Add("RequestVerificationToken", saveToken ?? string.Empty);
        var before = await viewerClient.PostAsync("/targets/save",
            new StringContent("{\"contentTypeId\":1}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        // Promote to Manager (Target.Edit granted through the Master User screen).
        var result = await PostJsonAsync(client, "/Admin/Users/Update", new
        {
            userId,
            roleName = AuthConstants.Roles.Manager,
            isActive = true
        }, token);
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync("promote.target1");
        Assert.NotNull(user);
        Assert.True(await userManager.IsInRoleAsync(user!, AuthConstants.Roles.Manager));

        // New sign-in picks up the new role: no longer a 403 (reaches business validation).
        var managerClient = await _factory.SignInAsync("promote.target1", AdminPassword);
        var managerTargetsPage = await managerClient.GetAsync("/Targets");
        var managerToken = AuthTestFactory.ExtractAntiforgeryToken(await managerTargetsPage.Content.ReadAsStringAsync());
        managerClient.DefaultRequestHeaders.Add("RequestVerificationToken", managerToken ?? string.Empty);
        var after = await managerClient.PostAsync("/targets/save",
            new StringContent("{\"contentTypeId\":1}", Encoding.UTF8, "application/json"));
        Assert.NotEqual(HttpStatusCode.Forbidden, after.StatusCode);
    }

    // ------------------------------------------------------------------
    // 20. Unauthorized users cannot access admin URLs directly
    // ------------------------------------------------------------------

    [Fact]
    public async Task AdminUrls_AreProtected_FromViewersAndAnonymous()
    {
        await _factory.CreateRoleUserAsync("guard.viewer", AuthConstants.Roles.Viewer, AdminPassword);
        // auto-redirect disabled so the AccessDenied redirect itself is observable.
        var viewerClient = await _factory.SignInAsync("guard.viewer", AdminPassword, allowAutoRedirect: false);

        // Master User + Master Roles + Admin TikTok: viewer is redirected to AccessDenied.
        foreach (var url in new[] { "/Admin/Users", "/Admin/Roles", "/Admin/TikTok" })
        {
            var response = await viewerClient.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location?.ToString() ?? string.Empty);
        }

        // Anonymous requests are redirected to Login (fallback policy).
        var anonymous = _factory.CreateBrowserClient(allowAutoRedirect: false);
        var anonymousResponse = await anonymous.GetAsync("/Admin/Users");
        Assert.Contains("/Account/Login", anonymousResponse.Headers.Location?.ToString() ?? string.Empty);

        // JSON mutation attempts receive 403 (never an HTML redirect).
        viewerClient.DefaultRequestHeaders.Remove("RequestVerificationToken");
        viewerClient.DefaultRequestHeaders.Add("RequestVerificationToken", "irrelevant");
        var jsonAttempt = await viewerClient.PostAsync("/Admin/Users/Create",
            new StringContent("{\"userName\":\"x\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, jsonAttempt.StatusCode);
    }

    // ------------------------------------------------------------------
    // Admin TikTok is now permission-gated for administrators
    // ------------------------------------------------------------------

    [Fact]
    public async Task AdminTikTok_IsGatedByTikTokAdminViewPermission()
    {
        var (client, _) = await SignInAdminAsync("tiktok.admin");
        var page = await client.GetAsync("/Admin/TikTok");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }
}

/// <summary>
/// Role &amp; Permission management tests. Uses dedicated factory instances so the
/// last-admin safety scenarios see an isolated user store (the acting admin must be
/// the only administrator / permission holder in those scenarios).
/// </summary>
public class AdminRolePermissionIntegrationTests : IDisposable
{
    private readonly AuthTestFactory _factory = new();
    private const string AdminPassword = "Admin-Passw0rd!X";

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpClient Client, string Token)> SignInAdminAsync(string username)
    {
        await _factory.CreateRoleUserAsync(username, AuthConstants.Roles.Administrator, AdminPassword);
        var client = await _factory.SignInAsync(username, AdminPassword, allowAutoRedirect: true);
        var page = await client.GetAsync("/Admin/Roles");
        page.EnsureSuccessStatusCode();
        return (client, AuthTestFactory.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync()) ?? string.Empty);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object payload, string token)
    {
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        var response = await client.PostAsync(url,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement;
    }

    private async Task<ApplicationRole> GetRoleAsync(string roleName)
    {
        using var scope = _factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(roleName);
        Assert.NotNull(role);
        return role!;
    }

    /// <summary>
    /// Finds or creates a built-in role with its default permission claims
    /// (mirrors AdminSeeder) - tests may run before any user was created.
    /// </summary>
    private async Task<ApplicationRole> EnsureRoleAsync(string roleName)
    {
        using var scope = _factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(roleName);
        if (role is null)
        {
            role = new ApplicationRole(roleName);
            Assert.True((await roleManager.CreateAsync(role)).Succeeded);

            var desired = roleName == AuthConstants.Roles.Administrator
                ? AuthConstants.AllKnownPermissions.ToArray()
                : AuthConstants.DefaultRolePermissions.TryGetValue(roleName, out var perms)
                    ? perms
                    : Array.Empty<string>();
            foreach (var permission in desired)
            {
                Assert.True((await roleManager.AddClaimAsync(role,
                    new System.Security.Claims.Claim(AuthConstants.PermissionClaimType, permission))).Succeeded);
            }
        }

        return role!;
    }

    private async Task<HashSet<string>> GetRolePermissionsAsync(string roleName)
    {
        using var scope = _factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(roleName);
        Assert.NotNull(role);
        var claims = await roleManager.GetClaimsAsync(role!);
        return claims
            .Where(c => c.Type == AuthConstants.PermissionClaimType)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------
    // 14/15. Assign permission to role - stored in AspNetRoleClaims
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_CanAssignPermission_ClaimStoredInRoleClaims()
    {
        var (client, token) = await SignInAdminAsync("perm.admin");
        var role = await EnsureRoleAsync(AuthConstants.Roles.Viewer);
        Assert.DoesNotContain(AuthConstants.Permissions.TargetEdit, await GetRolePermissionsAsync(role.Name!));

        var requested = AuthConstants.AllKnownPermissions.ToArray();
        var result = await PostJsonAsync(client, "/Admin/Roles/SetPermissions", new
        {
            roleId = role.Id,
            permissions = requested
        }, token);

        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        var after = await GetRolePermissionsAsync(role.Name!);
        Assert.Contains(AuthConstants.Permissions.TargetEdit, after);

        // 15. The claim is persisted in the existing AspNetRoleClaims table.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(await db.RoleClaims.ToListAsync(), c =>
            c.RoleId == role.Id &&
            c.ClaimType == AuthConstants.PermissionClaimType &&
            c.ClaimValue == AuthConstants.Permissions.TargetEdit);
    }

    // ------------------------------------------------------------------
    // 16/17. Permission effect: holder can act, non-holder is denied
    // ------------------------------------------------------------------

    [Fact]
    public async Task Viewer_GainsTargetEdit_CanThenPostTargetSave()
    {
        var (client, token) = await SignInAdminAsync("effect.admin");
        var role = await EnsureRoleAsync(AuthConstants.Roles.Viewer);
        await _factory.CreateRoleUserAsync("effect.viewer1", AuthConstants.Roles.Viewer, AdminPassword);

        // Before: viewer POST /targets/save -> 403 (authorization layer).
        var beforeClient = await _factory.SignInAsync("effect.viewer1", AdminPassword);
        var beforePage = await beforeClient.GetAsync("/Targets");
        var beforeToken = AuthTestFactory.ExtractAntiforgeryToken(await beforePage.Content.ReadAsStringAsync());
        beforeClient.DefaultRequestHeaders.Add("RequestVerificationToken", beforeToken ?? string.Empty);
        var before = await beforeClient.PostAsync("/targets/save",
            new StringContent("{\"contentTypeId\":1}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        // Grant Target.Edit to the Viewer role through the Master Roles screen.
        var result = await PostJsonAsync(client, "/Admin/Roles/SetPermissions", new
        {
            roleId = role.Id,
            permissions = AuthConstants.AllKnownPermissions.ToArray()
        }, token);
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());

        // After: fresh sign-in carries the new claim; no longer 403 (business validation instead).
        var afterClient = await _factory.SignInAsync("effect.viewer1", AdminPassword);
        var afterPage = await afterClient.GetAsync("/Targets");
        var afterToken = AuthTestFactory.ExtractAntiforgeryToken(await afterPage.Content.ReadAsStringAsync());
        afterClient.DefaultRequestHeaders.Add("RequestVerificationToken", afterToken ?? string.Empty);
        var after = await afterClient.PostAsync("/targets/save",
            new StringContent("{\"contentTypeId\":1}", Encoding.UTF8, "application/json"));
        Assert.NotEqual(HttpStatusCode.Forbidden, after.StatusCode);
    }

    // ------------------------------------------------------------------
    // 18/19. Last-admin safeguards
    // ------------------------------------------------------------------

    [Fact]
    public async Task LastAdministrator_CannotBeDeactivated_OrDemoted()
    {
        // Isolated store: this admin is the ONLY user (created inside SignInAdminAsync).
        var (client, token) = await SignInAdminAsync("last.admin");
        string userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            userId = (await userManager.FindByNameAsync("last.admin"))!.Id;
        }

        // Deactivation rejected.
        var deactivate = await PostJsonAsync(client, "/Admin/Users/SetActive", new { userId, isActive = false }, token);
        Assert.False(deactivate.GetProperty("success").GetBoolean());
        Assert.Equal("LAST_ADMIN", deactivate.GetProperty("errorCode").GetString());

        // Materialize the Viewer role (demotion target) before testing the demote guard.
        await _factory.CreateRoleUserAsync("last.viewer", AuthConstants.Roles.Viewer, AdminPassword);

        // Demotion rejected.
        var demote = await PostJsonAsync(client, "/Admin/Users/Update", new
        {
            userId,
            roleName = AuthConstants.Roles.Viewer,
            isActive = true
        }, token);
        Assert.False(demote.GetProperty("success").GetBoolean());
        Assert.Equal("LAST_ADMIN", demote.GetProperty("errorCode").GetString());

        // With a second active administrator, deactivating one is allowed.
        var secondUserId = await _factory.CreateRoleUserAsync("second.admin", AuthConstants.Roles.Administrator, AdminPassword);
        var deactivateSecond = await PostJsonAsync(client, "/Admin/Users/SetActive", new { userId = secondUserId, isActive = false }, token);
        Assert.True(deactivateSecond.GetProperty("success").GetBoolean(), deactivateSecond.GetRawText());
    }

    [Fact]
    public async Task AdministratorRole_CannotLoseUserManage_WhenNoOtherHolderExists()
    {
        // The acting admin (created inside SignInAdminAsync) is the ONLY user/holder.
        var (client, token) = await SignInAdminAsync("solo.admin");
        var role = await EnsureRoleAsync(AuthConstants.Roles.Administrator);

        // Removing User.Manage from the Administrator role would leave no active holder.
        var withoutUserManage = AuthConstants.AllKnownPermissions
            .Where(p => p != AuthConstants.Permissions.UserManage)
            .ToArray();
        var result = await PostJsonAsync(client, "/Admin/Roles/SetPermissions", new
        {
            roleId = role.Id,
            permissions = withoutUserManage
        }, token);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("LAST_ADMIN", result.GetProperty("errorCode").GetString());
        Assert.Contains(AuthConstants.Permissions.UserManage, await GetRolePermissionsAsync(role.Name!));

        // A second administrator in the SAME role does not create an independent
        // holder - removal is still rejected (both would lose User.Manage at once).
        await _factory.CreateRoleUserAsync("backup.admin", AuthConstants.Roles.Administrator, AdminPassword);
        var retry = await PostJsonAsync(client, "/Admin/Roles/SetPermissions", new
        {
            roleId = role.Id,
            permissions = withoutUserManage
        }, token);
        Assert.False(retry.GetProperty("success").GetBoolean());
        Assert.Contains(AuthConstants.Permissions.UserManage, await GetRolePermissionsAsync(role.Name!));

        // Creating an independent holder (Manager role granted User.Manage with an
        // active member) makes the removal safe -> it succeeds.
        await _factory.CreateRoleUserAsync("holder.manager", AuthConstants.Roles.Manager, AdminPassword);
        var managerRole = await GetRoleAsync(AuthConstants.Roles.Manager);
        var grantManager = await PostJsonAsync(client, "/Admin/Roles/SetPermissions", new
        {
            roleId = managerRole.Id,
            permissions = AuthConstants.AllKnownPermissions.ToArray()
        }, token);
        Assert.True(grantManager.GetProperty("success").GetBoolean(), grantManager.GetRawText());

        var finalRetry = await PostJsonAsync(client, "/Admin/Roles/SetPermissions", new
        {
            roleId = role.Id,
            permissions = withoutUserManage
        }, token);
        Assert.True(finalRetry.GetProperty("success").GetBoolean(), finalRetry.GetRawText());
        Assert.DoesNotContain(AuthConstants.Permissions.UserManage, await GetRolePermissionsAsync(role.Name!));
    }

    // ------------------------------------------------------------------
    // Unknown permissions are never accepted from user input
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetPermissions_RejectsUnknownPermissionValues()
    {
        var (client, token) = await SignInAdminAsync("unknown.admin");
        var role = await EnsureRoleAsync(AuthConstants.Roles.Viewer);

        var result = await PostJsonAsync(client, "/Admin/Roles/SetPermissions", new
        {
            roleId = role.Id,
            permissions = new[] { AuthConstants.Permissions.TargetView, "Made.Up.Permission" }
        }, token);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("UNKNOWN_PERMISSION", result.GetProperty("errorCode").GetString());
    }
}

/// <summary>
/// Regression guards for the Admin Management change set: Target and Daily Summary
/// screens still work for an authenticated manager, and login remains intact.
/// (Deep business rules are covered by their existing suites.)
/// </summary>
public class AdminChangeRegressionTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;
    private const string AdminPassword = "Admin-Passw0rd!X";

    public AdminChangeRegressionTests(AuthTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Target_And_DailySummary_StillAccessibleAfterAdminChanges()
    {
        await _factory.CreateRoleUserAsync("regression.manager", AuthConstants.Roles.Manager, AdminPassword);
        var client = await _factory.SignInAsync("regression.manager", AdminPassword);

        var targets = await client.GetAsync("/Targets");
        Assert.Equal(HttpStatusCode.OK, targets.StatusCode);

        var dailySummary = await client.GetAsync("/DailySummary");
        Assert.Equal(HttpStatusCode.OK, dailySummary.StatusCode);

        var contentLog = await client.GetAsync("/ContentLog");
        Assert.Equal(HttpStatusCode.OK, contentLog.StatusCode);
    }

    [Fact]
    public async Task Sidebar_HidesAdminSection_ForNonAdmin_AndShowsItForAdmin()
    {
        await _factory.CreateRoleUserAsync("sidebar.viewer", AuthConstants.Roles.Viewer, AdminPassword);
        var viewerClient = await _factory.SignInAsync("sidebar.viewer", AdminPassword);
        var viewerHtml = await (await viewerClient.GetAsync("/Targets")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Master User", viewerHtml);
        Assert.DoesNotContain("Admin TikTok", viewerHtml);
        Assert.DoesNotContain("Master Role &amp; Permission", viewerHtml);

        await _factory.CreateRoleUserAsync("sidebar.admin", AuthConstants.Roles.Administrator, AdminPassword);
        var adminClient = await _factory.SignInAsync("sidebar.admin", AdminPassword);
        var adminHtml = await (await adminClient.GetAsync("/Targets")).Content.ReadAsStringAsync();
        Assert.Contains("Master User", adminHtml);
        Assert.Contains("Master Role &amp; Permission", adminHtml);
        Assert.Contains("Admin TikTok", adminHtml);
    }
}
