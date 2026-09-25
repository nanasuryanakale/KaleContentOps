using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.Auth;

/// <summary>
/// End-to-end validation of the Authentication &amp; Access Foundation over the real
/// pipeline (Identity + cookie auth + fallback policy + permission policies):
/// 1  Login valid -> 302 to the app, identity cookie issued.
/// 2  Login invalid -> rejected with error, no identity cookie.
/// 3  Logout -> back to unauthenticated.
/// 4  Anonymous internal page -> redirected to Login.
/// 5  Authorized user opens Target page + read endpoints.
/// 6  User without Target.Edit cannot mutate targets (backend 403, not just hidden UI).
/// 8  Existing Content Log / Daily Summary open after authentication.
/// </summary>
public class AuthFlowIntegrationTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;

    public AuthFlowIntegrationTests(AuthTestFactory factory)
    {
        _factory = factory;
    }

    private const string AdminPassword = "Admin-Passw0rd!X";

    // ------------------------------------------------------------------
    // 1/2. Login
    // ------------------------------------------------------------------

    [Fact]
    public async Task Login_ValidCredentials_IssuesCookieAndRedirects()
    {
        await _factory.CreateRoleUserAsync("flow.admin", AuthConstants.Roles.Administrator, AdminPassword);

        var client = _factory.CreateBrowserClient(allowAutoRedirect: false);
        var response = await client.PostAsync("/Account/Login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Username"] = "flow.admin",
                ["Password"] = AdminPassword,
                ["RememberMe"] = "false",
                ["__RequestVerificationToken"] = await GetLoginTokenAsync(client)
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/", response.Headers.Location!.ToString());
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies! : Enumerable.Empty<string>();
        Assert.Contains(setCookies, c => c.Contains(".AspNetCore.Identity.Application"));
    }

    [Fact]
    public async Task Login_InvalidPassword_IsRejectedWithoutCookie()
    {
        await _factory.CreateRoleUserAsync("flow.wrong", AuthConstants.Roles.Viewer, AdminPassword);

        var client = _factory.CreateBrowserClient(allowAutoRedirect: false);
        var response = await client.PostAsync("/Account/Login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Username"] = "flow.wrong",
                ["Password"] = "definitely-wrong!",
                ["__RequestVerificationToken"] = await GetLoginTokenAsync(client)
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // form redisplayed with error
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Username atau password salah", body);
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies! : Enumerable.Empty<string>();
        Assert.DoesNotContain(setCookies, c => c.Contains(".AspNetCore.Identity.Application"));
    }

    // ------------------------------------------------------------------
    // 3. Logout
    // ------------------------------------------------------------------

    [Fact]
    public async Task Logout_ReturnsUserToUnauthenticatedState()
    {
        await _factory.CreateRoleUserAsync("flow.out", AuthConstants.Roles.Viewer, AdminPassword);
        var client = await _factory.SignInAsync("flow.out", AdminPassword, allowAutoRedirect: false);

        // Authenticated: internal page opens.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Targets")).StatusCode);

        var homeHtml = await client.GetStringAsync("/");
        var token = AuthTestFactory.ExtractAntiforgeryToken(homeHtml);
        Assert.False(string.IsNullOrWhiteSpace(token));

        var logout = await client.PostAsync("/Account/Logout",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token!
            }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        // Same client is unauthenticated again: internal page redirects to Login.
        var afterLogout = await client.GetAsync("/Targets");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        Assert.Contains("/Account/Login", afterLogout.Headers.Location!.ToString());
    }

    // ------------------------------------------------------------------
    // 4. Anonymous access
    // ------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_InternalPage_RedirectsToLogin()
    {
        var client = _factory.CreateBrowserClient(allowAutoRedirect: false);

        var response = await client.GetAsync("/Targets");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Anonymous_TargetSave_Returns401NotRedirect()
    {
        var client = _factory.CreateBrowserClient(allowAutoRedirect: false);

        var response = await client.PostAsync("/targets/save",
            new StringContent("""{"ContentTypeId":2,"TargetUpload":1,"TargetViews":1}""", Encoding.UTF8, "application/json"));

        // JSON endpoint: 401, never an HTML login redirect (browser Accept header is absent).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_Page_IsPublic()
    {
        var client = _factory.CreateBrowserClient(allowAutoRedirect: false);

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // 5/6. Target read vs edit permissions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Viewer_CanOpenTargetPageAndReadEndpoints()
    {
        await _factory.CreateRoleUserAsync("flow.viewer", AuthConstants.Roles.Viewer, AdminPassword);
        var client = await _factory.SignInAsync("flow.viewer", AdminPassword);

        var page = await client.GetAsync("/Targets");
        Assert.True(page.IsSuccessStatusCode, $"GET /Targets -> {(int)page.StatusCode}");

        var current = await client.GetAsync("/targets/current");
        Assert.True(current.IsSuccessStatusCode, $"GET /targets/current -> {(int)current.StatusCode}");

        // Content type 2 may not exist in the shared test store; the endpoint
        // legitimately answers 404 ("Content type tidak ditemukan") in that case.
        var actual = await client.GetAsync("/targets/actual?contentTypeId=2");
        Assert.True(actual.IsSuccessStatusCode || actual.StatusCode == HttpStatusCode.NotFound,
            $"GET /targets/actual -> {(int)actual.StatusCode}");
    }

    [Fact]
    public async Task UserWithoutTargetEdit_CannotMutateTargets_BackendReturns403()
    {
        await _factory.CreateRoleUserAsync("flow.viewer2", AuthConstants.Roles.Viewer, AdminPassword);
        var client = await _factory.SignInAsync("flow.viewer2", AdminPassword, allowAutoRedirect: false);

        var homeHtml = await client.GetStringAsync("/");
        var token = AuthTestFactory.ExtractAntiforgeryToken(homeHtml);
        Assert.False(string.IsNullOrWhiteSpace(token));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/targets/save")
        {
            Content = new StringContent("""{"ContentTypeId":2,"TargetUpload":10,"TargetViews":100000}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RequestVerificationToken", token!);

        var response = await client.SendAsync(request);

        // Backend authorization gate (not just hidden UI): 403 even with a valid
        // antiforgery token and a well-formed payload.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Administrator_CanMutateTargets()
    {
        await SeedNonKkContentTypeAsync();
        await _factory.CreateRoleUserAsync("flow.admin2", AuthConstants.Roles.Administrator, AdminPassword);
        var client = await _factory.SignInAsync("flow.admin2", AdminPassword, allowAutoRedirect: false);

        var homeHtml = await client.GetStringAsync("/");
        var token = AuthTestFactory.ExtractAntiforgeryToken(homeHtml);
        Assert.False(string.IsNullOrWhiteSpace(token));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/targets/save")
        {
            Content = new StringContent("""{"ContentTypeId":2,"TargetUpload":10,"TargetViews":100000}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RequestVerificationToken", token!);

        var response = await client.SendAsync(request);

        // With Target.Edit + valid token + seeded NON_KK content type, the save succeeds.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // 8. Existing menus keep working after authentication
    // ------------------------------------------------------------------

    [Fact]
    public async Task AuthenticatedUser_CanOpenContentLogAndDailySummary()
    {
        await _factory.CreateRoleUserAsync("flow.mixed", AuthConstants.Roles.Administrator, AdminPassword);
        var client = await _factory.SignInAsync("flow.mixed", AdminPassword);

        Assert.True((await client.GetAsync("/ContentLog")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/DailySummary")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/")).IsSuccessStatusCode);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private async Task<string> GetLoginTokenAsync(HttpClient client)
    {
        var loginPage = await client.GetAsync("/Account/Login");
        loginPage.EnsureSuccessStatusCode();
        var token = AuthTestFactory.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(token), "Login page must carry an antiforgery token.");
        return token!;
    }

    private async Task SeedNonKkContentTypeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!db.ContentTypes.Any(c => c.Id == 2))
        {
            db.ContentTypes.Add(new KaleContentOps.Models.ContentType
            {
                Id = 2,
                Code = "NON_KK",
                Name = "Non-KK",
                Color = "blue",
                IsActive = true,
                CreatedAt = new DateTime(2026, 1, 1),
                UpdatedAt = new DateTime(2026, 1, 1)
            });
            await db.SaveChangesAsync();
        }
    }
}
