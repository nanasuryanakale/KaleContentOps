using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KaleContentOps.Tests;

/// <summary>
/// Shared WebApplicationFactory for authentication/authorization integration tests.
/// - Swaps SQL Server for EF InMemory (no real database required, unique store per instance).
/// - Disables the startup admin seeder (tests create their own users).
/// - Reuses the real Program.cs pipeline: Identity, cookie auth, fallback policy,
///   permission policies, MVC routing, antiforgery.
/// </summary>
public sealed class AuthTestFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"AuthTests_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Reliable config switch: Program.cs skips startup seeding when false.
        builder.UseSetting("Identity:SeedOnStartup", "false");

        builder.ConfigureServices(services =>
        {
            // Remove the SQL Server AppDbContext registration made in Program.cs.
            // EF Core 8+ splits AddDbContext into an options singleton plus one or more
            // IDbContextOptionsConfiguration<T> delegates - both must go, otherwise both
            // providers end up registered and EF throws.
            services.RemoveAll(typeof(IDbContextOptionsConfiguration<AppDbContext>));
            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));

            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            // Disable the startup seeder for the host under test.
            services.Configure<AdminSeedOptions>(o => o.Enabled = false);
        });
    }

    /// <summary>
    /// Creates a user (active by default) in the given role and returns its stable Id.
    /// The role is created with the default permission claims from AuthConstants,
    /// mirroring what AdminSeeder does on a real deployment.
    /// </summary>
    public async Task<string> CreateRoleUserAsync(string username, string role, string password = "Passw0rd!Long", bool isActive = true)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var existingRole = await roleManager.FindByNameAsync(role);
        if (existingRole is null)
        {
            existingRole = new ApplicationRole(role);
            Assert.True((await roleManager.CreateAsync(existingRole)).Succeeded);
        }

        // Attach default permission claims (Administrator -> full set).
        var existingClaims = await roleManager.GetClaimsAsync(existingRole);
        var owned = existingClaims
            .Where(c => c.Type == AuthConstants.PermissionClaimType)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        // Mirrors AdminSeeder: Administrator -> full known set; other built-in roles -> defaults.
        var desired = role == AuthConstants.Roles.Administrator
            ? AuthConstants.AllKnownPermissions.ToArray()
            : AuthConstants.DefaultRolePermissions.TryGetValue(role, out var perms)
                ? perms
                : Array.Empty<string>();

        foreach (var permission in desired.Where(p => !owned.Contains(p)))
        {
            Assert.True((await roleManager.AddClaimAsync(existingRole,
                new System.Security.Claims.Claim(AuthConstants.PermissionClaimType, permission))).Succeeded);
        }

        var user = new ApplicationUser
        {
            UserName = username,
            Email = $"{username}@test.local",
            EmailConfirmed = true,
            DisplayName = username,
            IsActive = isActive
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        await userManager.AddToRoleAsync(user, role);
        return user.Id;
    }

    /// <summary>
    /// Creates a client that mimics a browser navigation (Accept: text/html).
    /// Real browsers always send this header; the auth redirect handler relies on it.
    /// </summary>
    public HttpClient CreateBrowserClient(bool allowAutoRedirect = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        return client;
    }

    /// <summary>
    /// Signs a user in server-side and returns the authenticated HttpClient
    /// (identity cookie attached). The login POST itself never follows redirects,
    /// so the 302 post-login cannot be chased into a JSON 401 challenge loop.
    /// </summary>
    public async Task<HttpClient> SignInAsync(string username, string password = "Passw0rd!Long", bool allowAutoRedirect = true)
    {
        var client = CreateBrowserClient(allowAutoRedirect: false);
        var loginPage = await client.GetAsync("/Account/Login");
        loginPage.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

        var form = new Dictionary<string, string>
        {
            ["Username"] = username,
            ["Password"] = password,
            ["RememberMe"] = "false",
            ["__RequestVerificationToken"] = token ?? string.Empty
        };

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(form));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode); // success -> 302 to local app
        return client;
    }

    /// <summary>Extracts the MVC antiforgery hidden-field token from an HTML page.</summary>
    public static string? ExtractAntiforgeryToken(string html)
    {
        var marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            // Order of attributes can differ; try the reversed attribute order too.
            marker = "type=\"hidden\" value=\"";
            start = html.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }
        }
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return end < 0 ? null : html[start..end];
    }
}
