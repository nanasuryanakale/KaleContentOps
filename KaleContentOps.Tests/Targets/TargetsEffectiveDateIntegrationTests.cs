using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Security;
using KaleContentOps.Services.Targets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.Targets;

/// <summary>
/// Phase 4b end-to-end over the real pipeline (Identity auth + cookie + permission
/// policies + MVC routing + antiforgery, mirroring Program.cs):
/// - Effective Date save flows: new version, same-date revise (no duplicate), future scheduled,
///   backdate (historical periods keep the older target),
/// - ChangedByUserId is stamped from the authenticated principal,
/// - /targets/history requires Target.History.View (403 without it),
/// - /targets/save still 403 for users without Target.Edit,
/// - regression: /Targets page + existing read endpoints stay available to Viewer.
/// Uses the shared AuthTestFactory (InMemory store, unique per instance).
/// </summary>
public class TargetsEffectiveDateIntegrationTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;
    private const string Password = "Admin-Passw0rd!X";

    public TargetsEffectiveDateIntegrationTests(AuthTestFactory factory)
    {
        _factory = factory;
    }

    private const string SavePayloadTemplate =
        """{{"contentTypeId":2,"targetUpload":{0},"targetViews":{1},"effectiveDate":"{2:yyyy-MM-dd}"}}""";

    private static string SavePayload(int upload, long views, DateOnly effectiveDate) =>
        string.Format(SavePayloadTemplate, upload, views, effectiveDate);

    private static string SavePayloadNoDate(int upload, long views) =>
        $$"""{"contentTypeId":2,"targetUpload":{{upload}},"targetViews":{{views}}}""";

    private async Task<HttpClient> SignInAdminAsync(string username)
    {
        await SeedNonKkContentTypeAsync();
        await _factory.CreateRoleUserAsync(username, AuthConstants.Roles.Administrator, Password);
        return await _factory.SignInAsync(username, Password, allowAutoRedirect: false);
    }

    private async Task<(HttpClient Client, string Token)> SignedInWithTokenAsync(string username)
    {
        var client = await SignInAdminAsync(username);
        var page = await client.GetAsync("/Targets");
        Assert.True(page.IsSuccessStatusCode, $"GET /Targets -> {(int)page.StatusCode}");
        var token = AuthTestFactory.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(token), "/Targets must carry an antiforgery token.");
        return (client, token!);
    }

    /// <summary>
    /// The InMemory store is shared by all tests of this class (one factory fixture),
    /// so target-version save-flow tests clear the Targets table first to stay
    /// order-independent. Content Log / Daily Summary data are untouched.
    /// </summary>
    private async Task ResetTargetsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Targets.RemoveRange(db.Targets);
        await db.SaveChangesAsync();
    }

    /// <summary>Server-side "today" exactly as the application computes it (Asia/Jakarta).</summary>
    private async Task<DateOnly> GetAppTodayAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<Services.IShopTimeZone>().Today();
    }

    private async Task<HttpResponseMessage> PostSaveAsync(HttpClient client, string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/targets/save")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request);
    }

    // ============================================================
    // Effective Date save flows
    // ============================================================

    [Fact]
    public async Task Save_WithEffectiveDate_CreatesNewVersion_AndHistoryShowsBoth()
    {
        await ResetTargetsAsync();
        var (client, token) = await SignedInWithTokenAsync("eff.admin1");
        var aug1 = new DateOnly(2026, 8, 1);
        var sep1 = new DateOnly(2026, 9, 1);

        var first = await PostSaveAsync(client, token, SavePayload(10, 20_000, aug1));
        Assert.True(first.IsSuccessStatusCode, $"first save -> {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync()}");
        var second = await PostSaveAsync(client, token, SavePayload(15, 30_000, sep1));
        Assert.True(second.IsSuccessStatusCode, $"second save -> {(int)second.StatusCode}: {await second.Content.ReadAsStringAsync()}");

        var history = await client.GetFromJsonAsync<List<TargetHistoryItem>>("/targets/history?contentTypeId=2");
        Assert.NotNull(history);
        Assert.Equal(2, history!.Count);
        Assert.Equal(sep1, history[0].EffectiveDate);              // newest first
        Assert.Equal(aug1, history[1].EffectiveDate);
        Assert.Equal("eff.admin1", history[0].ChangedByName);      // stable Identity user resolved server-side
        Assert.Equal("eff.admin1", history[1].ChangedByName);
        Assert.NotNull(history[0].ChangedAt);
    }

    [Fact]
    public async Task Save_SameEffectiveDateTwice_Revises_NoDuplicateDate()
    {
        await ResetTargetsAsync();
        var (client, token) = await SignedInWithTokenAsync("eff.admin2");
        var date = new DateOnly(2026, 8, 1);

        Assert.True((await PostSaveAsync(client, token, SavePayload(10, 20_000, date))).IsSuccessStatusCode);
        var revise = await PostSaveAsync(client, token, SavePayload(18, 36_000, date));
        Assert.True(revise.IsSuccessStatusCode, $"revise -> {(int)revise.StatusCode}: {await revise.Content.ReadAsStringAsync()}");

        var payload = await revise.Content.ReadFromJsonAsync<TargetSaveResponseContract>();
        Assert.NotNull(payload);
        Assert.False(payload!.CreatedNewVersion);

        var history = await client.GetFromJsonAsync<List<TargetHistoryItem>>("/targets/history?contentTypeId=2");
        Assert.Single(history!);
        Assert.Equal(18, history![0].TargetUpload);
        Assert.Equal(36_000L, history[0].TargetViews);
    }

    [Fact]
    public async Task Save_FutureEffectiveDate_IsScheduled_CurrentStaysActive_SwitchesOnDate()
    {
        await ResetTargetsAsync();
        var (client, token) = await SignedInWithTokenAsync("eff.admin3");
        var today = await GetAppTodayAsync();
        var future = today.AddMonths(2);

        Assert.True((await PostSaveAsync(client, token, SavePayload(15, 30_000, today))).IsSuccessStatusCode);
        var scheduled = await PostSaveAsync(client, token, SavePayload(20, 40_000, future));
        Assert.True(scheduled.IsSuccessStatusCode, $"scheduled save -> {(int)scheduled.StatusCode}: {await scheduled.Content.ReadAsStringAsync()}");
        var payload = await scheduled.Content.ReadFromJsonAsync<TargetSaveResponseContract>();
        Assert.True(payload!.IsScheduled);

        // Current: today's version, NOT the scheduled one.
        var current = await client.GetFromJsonAsync<List<TargetCurrentContract>>("/targets/current");
        var nonKk = current!.Single(x => x.ContentTypeCode == "NON_KK");
        Assert.Equal(15, nonKk.TargetUpload);
        Assert.Equal(today, nonKk.EffectiveFrom);

        // Scheduled listing exposes the future version.
        var scheduledList = await client.GetFromJsonAsync<List<TargetCurrentContract>>("/targets/scheduled");
        var row = scheduledList!.Single(x => x.ContentTypeCode == "NON_KK");
        Assert.Equal(future, row.EffectiveFrom);
        Assert.Equal(20, row.TargetUpload);

        // Date-based resolution: before the future date -> 15/30.000; on the date -> 20/40.000.
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITargetService>();
        var dayBefore = await service.GetTargetForDateAsync(2, future.AddDays(-1));
        Assert.Equal(15, dayBefore!.TargetUpload);
        var onDate = await service.GetTargetForDateAsync(2, future);
        Assert.Equal(20, onDate!.TargetUpload);
    }

    [Fact]
    public async Task Save_BackdatedDate_HistoricalPeriodsKeepOlderTarget()
    {
        await ResetTargetsAsync();
        var (client, token) = await SignedInWithTokenAsync("eff.admin4");
        var today = await GetAppTodayAsync();
        var backdate = today.AddMonths(-1);

        Assert.True((await PostSaveAsync(client, token, SavePayload(10, 20_000, today))).IsSuccessStatusCode);
        Assert.True((await PostSaveAsync(client, token, SavePayload(5, 12_000, backdate))).IsSuccessStatusCode);

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITargetService>();

        var inBetween = await service.GetTargetForDateAsync(2, new DateOnly(2026, 9, 15));
        Assert.Equal(5, inBetween!.TargetUpload);
        Assert.Equal(12_000L, inBetween.TargetViews);

        var atToday = await service.GetTargetForDateAsync(2, today);
        Assert.Equal(10, atToday!.TargetUpload);
        Assert.Equal(20_000L, atToday.TargetViews);
    }

    [Fact]
    public async Task Save_WithoutEffectiveDate_LegacyBehaviorToday()
    {
        await ResetTargetsAsync();
        var (client, token) = await SignedInWithTokenAsync("eff.admin5");

        var response = await PostSaveAsync(client, token, SavePayloadNoDate(10, 20_000));
        Assert.True(response.IsSuccessStatusCode, $"legacy save -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var payload = await response.Content.ReadFromJsonAsync<TargetSaveResponseContract>();
        Assert.False(payload!.IsScheduled);
        Assert.Equal(await GetAppTodayAsync(), payload.EffectiveFrom); // server-clock date, not the browser's
    }

    // ============================================================
    // ChangedBy: server-side principal, not the request body
    // ============================================================

    [Fact]
    public async Task Save_ChangedByUserId_StampedFromAuthenticatedUser()
    {
        await ResetTargetsAsync();
        var (client, token) = await SignedInWithTokenAsync("eff.admin6");

        await PostSaveAsync(client, token, SavePayloadNoDate(9, 9_000));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = db.Targets.Single(t => t.ContentTypeId == 2);
        var expectedUserId = db.Users.Single(u => u.UserName == "eff.admin6").Id;

        Assert.Equal(expectedUserId, saved.ChangedByUserId); // stable Identity user id
        Assert.True(saved.UpdatedAt > DateTime.UtcNow.AddMinutes(-5));
    }

    // ============================================================
    // Authorization (backend, not just UI)
    // ============================================================

    [Fact]
    public async Task History_WithoutTargetHistoryView_Returns403()
    {
        await SeedNonKkContentTypeAsync();

        // Custom user WITHOUT Target.History.View: Viewer role is seeded WITH it
        // (AuthConstants.DefaultRolePermissions), so remove the claim explicitly.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Targets.RemoveRange(db.Targets);
            await db.SaveChangesAsync();
        }

        await _factory.CreateRoleUserAsync("eff.nohist", AuthConstants.Roles.Viewer, Password);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = db.Users.Single(u => u.UserName == "eff.nohist");
            var viewerRoleId = db.Roles.Single(r => r.Name == AuthConstants.Roles.Viewer).Id;
            var historyClaims = db.RoleClaims
                .Where(rc => rc.RoleId == viewerRoleId
                    && rc.ClaimType == AuthConstants.PermissionClaimType
                    && rc.ClaimValue == AuthConstants.Permissions.TargetHistoryView);
            db.RoleClaims.RemoveRange(historyClaims);
            await db.SaveChangesAsync();
        }

        var client = await _factory.SignInAsync("eff.nohist", Password, allowAutoRedirect: false);

        // Browser-style navigation (Accept: text/html): denial redirects to AccessDenied.
        var browserResponse = await client.GetAsync("/targets/history?contentTypeId=2");
        Assert.Equal(HttpStatusCode.Redirect, browserResponse.StatusCode);
        Assert.Contains("/Account/AccessDenied", browserResponse.Headers.Location!.ToString());

        // API-style request (no Accept: text/html): backend must answer 403 Forbidden.
        client.DefaultRequestHeaders.Accept.Clear();
        var apiResponse = await client.GetAsync("/targets/history?contentTypeId=2");
        Assert.Equal(HttpStatusCode.Forbidden, apiResponse.StatusCode);
    }

    [Fact]
    public async Task History_WithTargetHistoryView_Returns200()
    {
        var (client, _) = await SignedInWithTokenAsync("eff.admin7");

        var response = await client.GetAsync("/targets/history?contentTypeId=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Save_WithEffectiveDate_WithoutTargetEdit_Returns403()
    {
        await SeedNonKkContentTypeAsync();
        await _factory.CreateRoleUserAsync("eff.viewer2", AuthConstants.Roles.Viewer, Password);
        var client = await _factory.SignInAsync("eff.viewer2", Password, allowAutoRedirect: false);
        var homeHtml = await client.GetStringAsync("/");
        var token = AuthTestFactory.ExtractAntiforgeryToken(homeHtml);
        Assert.False(string.IsNullOrWhiteSpace(token));

        var response = await PostSaveAsync(client, token!, SavePayload(20, 40_000, new DateOnly(2026, 11, 1)));

        // Backend authorization gate, effective date or not.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Scheduled_ViewerCanRead_ButHistoryNeedsDedicatedPermission()
    {
        await SeedNonKkContentTypeAsync();
        await _factory.CreateRoleUserAsync("eff.viewer3", AuthConstants.Roles.Viewer, Password);
        var client = await _factory.SignInAsync("eff.viewer3", Password, allowAutoRedirect: false);

        // Scheduled targets are part of the Target page: Target.View suffices.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/targets/scheduled")).StatusCode);
        // Viewer is SEEDED with Target.History.View (default permission matrix) -> 200.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/targets/history?contentTypeId=2")).StatusCode);
    }

    // ============================================================
    // Regression: existing behavior stays intact
    // ============================================================

    [Fact]
    public async Task Regression_TargetsPageAndReadEndpointsStillWorkForViewer()
    {
        await SeedNonKkContentTypeAsync();
        await _factory.CreateRoleUserAsync("eff.viewer4", AuthConstants.Roles.Viewer, Password);
        var client = await _factory.SignInAsync("eff.viewer4", Password);

        Assert.True((await client.GetAsync("/Targets")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/targets/current")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/targets/scheduled")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/ContentLog")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/DailySummary")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Regression_ExistingTargetsStillResolveAfterEffectiveDateSaves()
    {
        await ResetTargetsAsync();
        var (client, token) = await SignedInWithTokenAsync("eff.admin8");
        var today = await GetAppTodayAsync();

        // Mixed saves: legacy (no date) + effective-date + scheduled.
        Assert.True((await PostSaveAsync(client, token, SavePayloadNoDate(10, 20_000))).IsSuccessStatusCode);
        Assert.True((await PostSaveAsync(client, token, SavePayload(5, 12_000, today.AddMonths(-1)))).IsSuccessStatusCode);
        Assert.True((await PostSaveAsync(client, token, SavePayload(20, 40_000, today.AddMonths(2)))).IsSuccessStatusCode);

        // One active row per content type must remain true (no active-row anomaly).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, db.Targets.Count(t => t.ContentTypeId == 2 && t.EffectiveTo == null));

        // Current shows today's effective values (today's version wins over backdate and scheduled).
        var current = await client.GetFromJsonAsync<List<TargetCurrentContract>>("/targets/current");
        var nonKk = current!.Single(x => x.ContentTypeCode == "NON_KK");
        Assert.Equal(today, nonKk.EffectiveFrom);
        Assert.Equal(10, nonKk.TargetUpload);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

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

    /// <summary>Minimal contract mirroring TargetSaveResponseDto JSON casing.</summary>
    private sealed class TargetSaveResponseContract
    {
        public bool Success { get; set; }
        public int ContentTypeId { get; set; }
        public string ContentTypeCode { get; set; } = string.Empty;
        public int TargetUpload { get; set; }
        public long TargetViews { get; set; }
        public DateOnly EffectiveFrom { get; set; }
        public DateOnly? EffectiveTo { get; set; }
        public bool CreatedNewVersion { get; set; }
        public bool IsScheduled { get; set; }
    }

    /// <summary>Minimal contract mirroring TargetCurrentItem JSON casing.</summary>
    private sealed class TargetCurrentContract
    {
        public int ContentTypeId { get; set; }
        public string ContentTypeCode { get; set; } = string.Empty;
        public string ContentTypeName { get; set; } = string.Empty;
        public int TargetUpload { get; set; }
        public long TargetViews { get; set; }
        public DateOnly EffectiveFrom { get; set; }
        public DateOnly? EffectiveTo { get; set; }
        public string? ChangedByName { get; set; }
    }
}
