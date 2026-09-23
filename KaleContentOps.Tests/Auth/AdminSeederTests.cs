using System;
using System.Linq;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.Auth;

/// <summary>
/// Idempotency of the startup admin seeder (validation scenario 11):
/// - first run creates the admin + roles + permission claims,
/// - second run changes nothing and creates no duplicates,
/// - with InitialAdmin:Enabled=false nothing is created at all.
/// Each test gets its own factory + InMemory store so tests are order-independent.
/// </summary>
public class AdminSeederTests
{
    private static AdminSeedOptions Options(string username = "bootstrap.admin") => new()
    {
        Enabled = true,
        Username = username,
        Password = "First-Admin-Passw0rd!",
        DisplayName = "Bootstrap Admin"
    };

    private static async Task<AdminSeedResult> RunSeederAsync(AuthTestFactory factory, AdminSeedOptions options)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return await AdminSeeder.SeedAsync(db, userManager, roleManager, options);
    }

    [Fact]
    public async Task FirstRun_CreatesRolesClaimsAndAdmin()
    {
        using var factory = new AuthTestFactory();
        var options = Options();

        var result = await RunSeederAsync(factory, options);

        Assert.False(result.Disabled);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.RolesCreated); // Administrator, Manager, Viewer
        Assert.True(result.AdminCreated);
        Assert.True(result.RoleClaimsAdded > 0);

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var admin = await userManager.FindByNameAsync(options.Username!);

        Assert.NotNull(admin);
        Assert.True(await userManager.IsInRoleAsync(admin!, AuthConstants.Roles.Administrator));

        // Administrator receives the full permission set via role claims.
        var adminRole = await roleManager.FindByNameAsync(AuthConstants.Roles.Administrator);
        var roleClaims = await roleManager.GetClaimsAsync(adminRole!);
        Assert.Contains(roleClaims, c => c.Type == AuthConstants.PermissionClaimType && c.Value == AuthConstants.Permissions.TargetView);
        Assert.Contains(roleClaims, c => c.Type == AuthConstants.PermissionClaimType && c.Value == AuthConstants.Permissions.TargetEdit);
        Assert.Contains(roleClaims, c => c.Type == AuthConstants.PermissionClaimType && c.Value == AuthConstants.Permissions.TargetHistoryView);
    }

    [Fact]
    public async Task SecondRun_DoesNotDuplicateAnything()
    {
        using var factory = new AuthTestFactory();
        var options = Options(username: "idempotent.admin");

        var first = await RunSeederAsync(factory, options);
        Assert.True(first.AdminCreated);

        var second = await RunSeederAsync(factory, options);

        Assert.False(second.AdminCreated);
        Assert.True(second.AdminAlreadyExists);
        Assert.Equal(0, second.RolesCreated);
        Assert.Equal(0, second.RoleClaimsAdded);
        Assert.Empty(second.Errors);

        // Exactly one user with that username exists.
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admins = await userManager.GetUsersInRoleAsync(AuthConstants.Roles.Administrator);
        Assert.Equal(1, admins.Count(u => u.UserName == options.Username));
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        using var factory = new AuthTestFactory();

        var result = await RunSeederAsync(factory, new AdminSeedOptions { Enabled = false });

        Assert.True(result.Disabled);
        Assert.False(result.AdminCreated);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task MissingCredentials_ReportsErrorWithoutThrowingAndWithoutCreatingUser()
    {
        using var factory = new AuthTestFactory();

        // No InitialAdmin credentials configured -> seeder reports instead of throwing.
        var result = await RunSeederAsync(factory, new AdminSeedOptions { Enabled = true });

        Assert.False(result.AdminCreated);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("InitialAdmin"));
    }
}
