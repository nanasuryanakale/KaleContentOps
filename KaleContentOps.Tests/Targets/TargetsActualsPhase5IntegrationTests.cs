using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.Targets;

/// <summary>
/// Phase 5 end-to-end over the real pipeline (Identity auth + permission policies +
/// MVC routing, mirroring Program.cs), following the TargetsEffectiveDateIntegrationTests
/// pattern: GET /targets/actuals returns the rolling 7-day window (GMT+7), per-type
/// actuals (latest metric per log, no double count), the date-resolved target, and the
/// derived selisih + combined status. Authorization: Target.View suffices (read-only).
/// Uses the shared AuthTestFactory (EF InMemory store, unique per fixture).
/// </summary>
public class TargetsActualsPhase5IntegrationTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;
    private const string Password = "Admin-Passw0rd!X";

    public TargetsActualsPhase5IntegrationTests(AuthTestFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> SignInAdminAsync(string username)
    {
        await SeedContentTypesAsync();
        await _factory.CreateRoleUserAsync(username, AuthConstants.Roles.Administrator, Password);
        return await _factory.SignInAsync(username, Password, allowAutoRedirect: false);
    }

    /// <summary>Server-side "today" exactly as the application computes it (Asia/Jakarta).</summary>
    private async Task<DateOnly> GetAppTodayAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<Services.IShopTimeZone>().Today();
    }

    /// <summary>
    /// The InMemory store is shared by all tests of this class: actual/target data are
    /// cleared first so tests stay order-independent. ContentTypes are kept.
    /// </summary>
    private async Task ResetDataAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ContentMetrics.RemoveRange(db.ContentMetrics);
        db.ContentLogs.RemoveRange(db.ContentLogs);
        db.Targets.RemoveRange(db.Targets);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Ensures the seeded content types exist (HasData normally provides them;
    /// guard keeps the test order-independent like SeedNonKkContentTypeAsync).
    /// </summary>
    private async Task SeedContentTypesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeds = new[]
        {
            (Id: 1, Code: "KK", Name: "Keranjang Kuning"),
            (Id: 2, Code: "NON_KK", Name: "Non-KK"),
            (Id: 3, Code: "AUTO_GMV_LIVE", Name: "Auto GMV Live")
        };
        foreach (var (id, code, name) in seeds)
        {
            if (db.ContentTypes.Any(c => c.Code == code))
            {
                continue;
            }

            db.ContentTypes.Add(new ContentType
            {
                Id = id,
                Code = code,
                Name = name,
                Color = code == "KK" ? "yellow" : code == "NON_KK" ? "blue" : "purple",
                IsActive = true,
                CreatedAt = new DateTime(2026, 1, 1),
                UpdatedAt = new DateTime(2026, 1, 1)
            });
        }

        await db.SaveChangesAsync();
    }

    private static ContentLog MakeLog(string videoId, DateOnly postDate, int? contentTypeId, bool? archived = null) =>
        new()
        {
            VideoId = videoId,
            TikTokShopId = null,
            VideoPostTime = postDate.ToDateTime(new TimeOnly(10, 0)),
            ContentTypeId = contentTypeId,
            IsArchived = archived,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    // ============================================================
    // Window + shape
    // ============================================================

    [Fact]
    public async Task Actuals_ReturnsRollingSevenDayWindow_NonKkFirst()
    {
        await ResetDataAsync();
        await SeedContentTypesAsync();
        var client = await SignInAdminAsync("p5.admin1");
        var today = await GetAppTodayAsync();

        var summary = await client.GetFromJsonAsync<TargetActualsSummaryContract>("/targets/actuals");

        Assert.NotNull(summary);
        Assert.Equal(today.AddDays(-6), summary!.StartDate); // D-6
        Assert.Equal(today, summary.EndDate);                // D
        Assert.Equal(7, summary.Days);
        Assert.Equal(2, summary.Items.Count);                // NON_KK + KK only
        Assert.Equal("NON_KK", summary.Items[0].ContentTypeCode);
        Assert.Equal("KK", summary.Items[1].ContentTypeCode);
    }

    // ============================================================
    // Actual Upload + Actual Views over the real pipeline
    // ============================================================

    [Fact]
    public async Task Actuals_CountsUploads_AndLatestViewsOnly()
    {
        await ResetDataAsync();
        await SeedContentTypesAsync();
        var client = await SignInAdminAsync("p5.admin2");
        var today = await GetAppTodayAsync();

        var logInWindow1 = MakeLog($"p5-a1-{Guid.NewGuid():N}", today, contentTypeId: 2);
        var logInWindow2 = MakeLog($"p5-a2-{Guid.NewGuid():N}", today.AddDays(-1), contentTypeId: 2);
        var logOutOfWindow = MakeLog($"p5-a3-{Guid.NewGuid():N}", today.AddDays(-8), contentTypeId: 2);
        var logArchived = MakeLog($"p5-a4-{Guid.NewGuid():N}", today, contentTypeId: 2, archived: true);
        var logUnclassified = MakeLog($"p5-a5-{Guid.NewGuid():N}", today, contentTypeId: null);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ContentLogs.AddRange(logInWindow1, logInWindow2, logOutOfWindow, logArchived, logUnclassified);
            await db.SaveChangesAsync();

            db.ContentMetrics.AddRange(
                // Two snapshots for logInWindow1: latest (today) must win over yesterday's.
                new ContentMetric { ContentLogId = logInWindow1.Id, Views = 100, CapturedAt = today.AddDays(-1).ToDateTime(new TimeOnly(12, 0)) },
                new ContentMetric { ContentLogId = logInWindow1.Id, Views = 180, CapturedAt = today.ToDateTime(new TimeOnly(12, 0)) },
                // One snapshot for logInWindow2.
                new ContentMetric { ContentLogId = logInWindow2.Id, Views = 250, CapturedAt = today.ToDateTime(new TimeOnly(12, 0)) });
            await db.SaveChangesAsync();
        }

        var summary = await client.GetFromJsonAsync<TargetActualsSummaryContract>("/targets/actuals");

        var nonKk = summary!.Items.Single(x => x.ContentTypeCode == "NON_KK");
        Assert.Equal(2, nonKk.ActualUpload);  // archived + unclassified + out-of-window excluded
        Assert.Equal(430, nonKk.ActualViews); // 180 (latest) + 250; never 100+180+250
    }

    // ============================================================
    // Target + Selisih + Status through save -> summary
    // ============================================================

    [Fact]
    public async Task Actuals_WithSavedTarget_ShowsTargetSelisihAndCombinedStatus()
    {
        await ResetDataAsync();
        await SeedContentTypesAsync();
        var client = await SignInAdminAsync("p5.admin3");
        var today = await GetAppTodayAsync();

        // Save a target via the real auto-save endpoint: 5 uploads / 1.000 views.
        var page = await client.GetAsync("/Targets");
        Assert.True(page.IsSuccessStatusCode, $"GET /Targets -> {(int)page.StatusCode}");
        var token = AuthTestFactory.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(token));

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/targets/save")
        {
            Content = new StringContent(
                $@"{{""contentTypeId"":2,""targetUpload"":5,""targetViews"":1000,""effectiveDate"":""{today:yyyy-MM-dd}""}}",
                Encoding.UTF8, "application/json")
        })
        {
            request.Headers.Add("RequestVerificationToken", token!);
            var save = await client.SendAsync(request);
            Assert.True(save.IsSuccessStatusCode, $"save -> {(int)save.StatusCode}: {await save.Content.ReadAsStringAsync()}");
        }

        // Actual: 1 upload with 2.000 views -> upload below target, views above.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var log = MakeLog($"p5-b1-{Guid.NewGuid():N}", today, contentTypeId: 2);
            db.ContentLogs.Add(log);
            await db.SaveChangesAsync();
            db.ContentMetrics.Add(new ContentMetric { ContentLogId = log.Id, Views = 2_000, CapturedAt = today.ToDateTime(new TimeOnly(12, 0)) });
            await db.SaveChangesAsync();
        }

        var summary = await client.GetFromJsonAsync<TargetActualsSummaryContract>("/targets/actuals");

        var nonKk = summary!.Items.Single(x => x.ContentTypeCode == "NON_KK");
        Assert.Equal(5, nonKk.TargetUpload);
        Assert.Equal(1_000, nonKk.TargetViews);
        Assert.Equal(today, nonKk.TargetEffectiveFrom);
        Assert.Equal(1, nonKk.ActualUpload);
        Assert.Equal(2_000, nonKk.ActualViews);
        Assert.Equal(-4, nonKk.SelisihUpload);     // negatives preserved
        Assert.Equal(1_000, nonKk.SelisihViews);
        Assert.Equal("Belum Tercapai", nonKk.Status); // one metric below -> combined Belum Tercapai

        // KK has no target yet: zeros placeholder, actuals still shown.
        var kk = summary.Items.Single(x => x.ContentTypeCode == "KK");
        Assert.Equal(0, kk.TargetUpload);
        Assert.Null(kk.TargetEffectiveFrom);
    }

    // ============================================================
    // Authorization: Target.View suffices for the read endpoint
    // ============================================================

    [Fact]
    public async Task Actuals_ViewerWithTargetView_CanRead()
    {
        await ResetDataAsync();
        await SeedContentTypesAsync();
        await _factory.CreateRoleUserAsync("p5.viewer", AuthConstants.Roles.Viewer, Password);
        var client = await _factory.SignInAsync("p5.viewer", Password);

        var response = await client.GetAsync("/targets/actuals");

        Assert.True(response.IsSuccessStatusCode, $"GET /targets/actuals -> {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Actuals_WithoutTargetView_Returns403()
    {
        await ResetDataAsync();
        await SeedContentTypesAsync();
        await _factory.CreateRoleUserAsync("p5.noview", AuthConstants.Roles.Viewer, Password);

        // Remove Target.View from the Viewer role (it is seeded with it by default).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var viewerRoleId = db.Roles.Single(r => r.Name == AuthConstants.Roles.Viewer).Id;
            var viewClaims = db.RoleClaims
                .Where(rc => rc.RoleId == viewerRoleId
                    && rc.ClaimType == AuthConstants.PermissionClaimType
                    && rc.ClaimValue == AuthConstants.Permissions.TargetView);
            db.RoleClaims.RemoveRange(viewClaims);
            await db.SaveChangesAsync();
        }

        var client = await _factory.SignInAsync("p5.noview", Password, allowAutoRedirect: false);
        client.DefaultRequestHeaders.Accept.Clear(); // API-style request -> 403, not redirect

        var response = await client.GetAsync("/targets/actuals");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Actuals_Anonymous_GetWithHtmlAccept_RedirectsToLogin_LikeExistingEndpoints()
    {
        // Browser-style navigation (Accept: text/html) on a GET: the auth challenge is an
        // HTML redirect to /Account/Login, matching the existing /Targets page behavior.
        // (API-style requests without Accept: text/html receive 401 instead.)
        var client = _factory.CreateBrowserClient(allowAutoRedirect: false);

        var response = await client.GetAsync("/targets/actuals");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Actuals_Anonymous_ApiStyleClient_Returns401NotRedirect()
    {
        // No Accept: text/html -> JSON endpoint challenge: 401 (same as POST /targets/save).
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/targets/actuals");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Contracts mirroring the JSON casing of the Phase 5 read models
    // ------------------------------------------------------------------

    private sealed class TargetActualsSummaryContract
    {
        public DateOnly StartDate { get; set; }
        public DateOnly EndDate { get; set; }
        public int Days { get; set; }
        public string TimeZoneId { get; set; } = string.Empty;
        public List<TargetActualItemContract> Items { get; set; } = new();
    }

    private sealed class TargetActualItemContract
    {
        public int ContentTypeId { get; set; }
        public string ContentTypeCode { get; set; } = string.Empty;
        public string ContentTypeName { get; set; } = string.Empty;
        public int ActualUpload { get; set; }
        public long ActualViews { get; set; }
        public int TargetUpload { get; set; }
        public long TargetViews { get; set; }
        public DateOnly? TargetEffectiveFrom { get; set; }
        public int SelisihUpload { get; set; }
        public long SelisihViews { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
