using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Services.TikTok;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KaleContentOps.Tests.TikTok.BatchSync;

/// <summary>
/// End-to-end endpoint/UI tests through the REAL MVC pipeline (AuthTestFactory):
/// authorization, antiforgery, JSON status/history and the Admin TikTok UI card.
/// The batch-sync background worker is disabled in this host, and no TikTok API is called.
/// </summary>
public class BatchSyncControllerTests
{
    private const string StartPath = "/Admin/TikTok/StartBatchSync";
    private const string StatusPath = "/Admin/TikTok/BatchSyncStatus/1";
    private const string HistoryPath = "/Admin/TikTok/BatchSyncHistory?take=5";

    private static async Task<(AuthTestFactory Factory, HttpClient Client)> CreateAdminAsync()
    {
        var factory = new AuthTestFactory();
        var username = "batch.admin." + Guid.NewGuid().ToString("N")[..8];
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Administrator);
        var client = await factory.SignInAsync(username);
        return (factory, client);
    }

    private static async Task<long> SeedShopAsync(AuthTestFactory factory, string cipher = "SHOP-CTRL")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var shop = new TikTokShop { ShopCipher = cipher, ShopName = "Controller Shop", ShopId = "S9", ShopCode = "SC9" };
        db.TikTokShops.Add(shop);
        await db.SaveChangesAsync();
        return shop.Id;
    }

    private static string ExtractAntiForgeryToken(string html)
    {
        var token = AuthTestFactory.ExtractAntiforgeryToken(html);
        Assert.True(token != null, "antiforgery token not found in rendered page");
        return token!;
    }

    [Fact]
    public async Task BatchSyncWorker_Is_Disabled_In_Test_Host_And_Service_Is_Resolvable()
    {
        using var factory = new AuthTestFactory();

        // Service registered unconditionally (Program.cs).
        Assert.NotNull(factory.Services.GetRequiredService<TikTokBatchSyncService>());

        // Worker disabled via TikTok:EnableBatchSyncWorker=false in AuthTestFactory,
        // so no hosted test process can ever reach the real TikTok API.
        var hosted = factory.Services.GetServices<IHostedService>().ToList();
        Assert.DoesNotContain(hosted, h => h is TikTokBatchSyncWorker);
    }

    [Fact]
    public async Task Anonymous_Status_Request_Redirects_To_Login()
    {
        using var factory = new AuthTestFactory();
        var client = factory.CreateBrowserClient(allowAutoRedirect: false);

        var response = await client.GetAsync(StatusPath);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task User_Without_TikTokAdmin_Permission_Is_Denied()
    {
        var factory = new AuthTestFactory();
        using (factory)
        {
            var username = "batch.viewer." + Guid.NewGuid().ToString("N")[..8];
            await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
            var client = await factory.SignInAsync(username);

            // Browser-style navigation: redirected to AccessDenied (existing app convention).
            var browserResponse = await client.GetAsync(StatusPath);
            Assert.Equal(HttpStatusCode.Redirect, browserResponse.StatusCode);
            Assert.Contains("/Account/AccessDenied", browserResponse.Headers.Location!.ToString());

            // API-style request: 403 Forbidden (existing app convention).
            client.DefaultRequestHeaders.Accept.Clear();
            var apiResponse = await client.GetAsync(StatusPath);
            Assert.Equal(HttpStatusCode.Forbidden, apiResponse.StatusCode);
        }
    }

    [Fact]
    public async Task StartBatchSync_Without_Antiforgery_Token_Returns_400()
    {
        var (factory, client) = await CreateAdminAsync();
        using (factory)
        {
            var shopId = await SeedShopAsync(factory);

            var response = await client.PostAsync(StartPath, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["shopId"] = shopId.ToString(),
                ["startDate"] = "2026-09-01",
                ["endDate"] = "2026-09-07"
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task StartBatchSync_With_Antiforgery_Returns_JobId_And_Status_Is_Readable()
    {
        var (factory, client) = await CreateAdminAsync();
        using (factory)
        {
            var shopId = await SeedShopAsync(factory);

            // Render the page first so the antiforgery cookie + token are issued.
            var page = await client.GetAsync("/Admin/TikTok");
            page.EnsureSuccessStatusCode();
            var token = ExtractAntiForgeryToken(await page.Content.ReadAsStringAsync());

            var post = await client.PostAsync(StartPath, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["shopId"] = shopId.ToString(),
                ["startDate"] = "2026-09-01",
                ["endDate"] = "2026-09-07",
                ["__RequestVerificationToken"] = token
            }));
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);

            using var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
            var jobId = doc.RootElement.GetProperty("jobId").GetInt64();
            Assert.True(jobId > 0);

            var statusResponse = await client.GetAsync($"/Admin/TikTok/BatchSyncStatus/{jobId}");
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

            using var statusDoc = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            var root = statusDoc.RootElement;
            Assert.Equal(jobId, root.GetProperty("jobId").GetInt64());
            Assert.Equal("Queued", root.GetProperty("status").GetString());
            Assert.Equal("Queued", root.GetProperty("phase").GetString());
            Assert.True(root.GetProperty("isActive").GetBoolean());
            Assert.Equal(0, root.GetProperty("totalVideoIdsFound").GetInt32());
        }
    }

    [Fact]
    public async Task StartBatchSync_Rejects_Invalid_Date_Range_With_400_And_Message()
    {
        var (factory, client) = await CreateAdminAsync();
        using (factory)
        {
            var shopId = await SeedShopAsync(factory);
            var page = await client.GetAsync("/Admin/TikTok");
            var token = ExtractAntiForgeryToken(await page.Content.ReadAsStringAsync());

            var post = await client.PostAsync(StartPath, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["shopId"] = shopId.ToString(),
                ["startDate"] = "2026-09-07",
                ["endDate"] = "2026-09-01",
                ["__RequestVerificationToken"] = token
            }));

            Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
            var body = await post.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Contains("End date", doc.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Missing_Status_Returns_404()
    {
        var (factory, client) = await CreateAdminAsync();
        using (factory)
        {
            var response = await client.GetAsync("/Admin/TikTok/BatchSyncStatus/999999");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task History_Returns_Json_Array()
    {
        var (factory, client) = await CreateAdminAsync();
        using (factory)
        {
            var response = await client.GetAsync(HistoryPath);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        }
    }

    [Fact]
    public async Task Admin_TikTok_Page_Renders_Batch_Sync_UI()
    {
        var (factory, client) = await CreateAdminAsync();
        using (factory)
        {
            var html = await (await client.GetAsync("/Admin/TikTok")).Content.ReadAsStringAsync();

            Assert.Contains("id=\"batchSyncForm\"", html);
            Assert.Contains("id=\"batchShopId\"", html);
            Assert.Contains("id=\"batchStartDate\"", html);
            Assert.Contains("id=\"batchEndDate\"", html);
            Assert.Contains("Sync Video Details", html);
            Assert.Contains("admin-tiktok-batch-sync.js", html);
            Assert.Contains("start_date_ge", html); // accurate period semantics in the label
            // Legacy sync buttons remain untouched.
            Assert.Contains("Sync Authorized Shops", html);
            Assert.Contains("Sync TikTok Videos", html);
        }
    }

    [Fact]
    public async Task Admin_TikTok_Date_Range_Card_Uses_Shared_Calendar_Chrome()
    {
        var (factory, client) = await CreateAdminAsync();
        using (factory)
        {
            var html = await (await client.GetAsync("/Admin/TikTok")).Content.ReadAsStringAsync();

            // Shared component stylesheet + the page chrome stylesheet (the missing piece that
            // previously made the picker render as an unstyled stack of buttons).
            Assert.Contains("css/calendar-popup.css", html);
            Assert.Contains("css/admin-tiktok.css", html);
            Assert.Contains("admin-tiktok-page", html);

            // Panel is contained by the positioned anchor (Daily Summary .header-anchor parity),
            // so absolute positioning anchors it under the trigger instead of the viewport.
            Assert.Contains("batch-date-anchor", html);
            var anchorIdx = html.IndexOf("batch-date-anchor", StringComparison.Ordinal);
            var panelIdx = html.IndexOf("id=\"batchDatePickerPanel\"", StringComparison.Ordinal);
            var sectionCloseIdx = html.IndexOf("</section>", anchorIdx, StringComparison.Ordinal);
            Assert.True(anchorIdx >= 0, "positioned anchor must be rendered");
            Assert.True(panelIdx > anchorIdx && panelIdx < sectionCloseIdx,
                "panel must live inside the positioned anchor");

            // Preset shortcuts match the shared Daily Summary set.
            foreach (var preset in new[] { "today", "yesterday", "7", "30", "90" })
            {
                Assert.Contains($"data-preset=\"{preset}\"", html);
            }
            Assert.Contains("Hari Ini", html);
            Assert.Contains("Kemarin", html);
            Assert.Contains("1 Minggu Terakhir", html);
            Assert.Contains("1 Bulan Terakhir", html);
            Assert.Contains("3 Bulan Terakhir", html);
            Assert.Contains("PERIODE CEPAT", html);
        }
    }

    [Fact]
    public async Task Admin_TikTok_Batch_Card_Uses_Scoped_Flex_Layout_Not_BS4_Helpers()
    {
        var (factory, client) = await CreateAdminAsync();
        using (factory)
        {
            var html = await (await client.GetAsync("/Admin/TikTok")).Content.ReadAsStringAsync();

            // The project ships Bootstrap 5.3, which does NOT define form-row / form-group /
            // form-inline. Those leftovers made the card render as unaligned blocks, so the
            // layout is now owned by the page-scoped stylesheet instead.
            Assert.DoesNotContain("form-row", html);
            Assert.DoesNotContain("form-inline", html);

            // Shop / date range / submit are laid out by one scoped flex row with consistent spacing.
            Assert.Contains("batch-sync-card", html);
            Assert.Contains("batch-sync-note", html);
            Assert.Contains("batch-sync-form", html);
            Assert.Contains("batch-form-grid", html);
            Assert.Contains("batch-field-shop", html);
            Assert.Contains("batch-field-range", html);
            Assert.Contains("batch-field-submit", html);
            // Visible labels replaced the Bootstrap-4-only sr-only/undefined form-group markup.
            Assert.Contains("batch-field-label", html);
            Assert.Contains("Periode analytics", html);

            // The history table scrolls inside its card instead of breaking the page, and shows
            // the never-attempted items as their own column (not as failures).
            Assert.Contains("batch-history-scroll", html);
            Assert.Contains("batch-history-table", html);
            Assert.Contains("Tidak diproses", html);

            // The calendar popup still anchors under its trigger, inside the positioned anchor.
            var anchorIdx = html.IndexOf("batch-date-anchor", StringComparison.Ordinal);
            var buttonIdx = html.IndexOf("id=\"batchDateRangeButton\"", StringComparison.Ordinal);
            var panelIdx = html.IndexOf("id=\"batchDatePickerPanel\"", StringComparison.Ordinal);
            Assert.True(anchorIdx >= 0 && buttonIdx > anchorIdx && panelIdx > buttonIdx,
                "panel must stay anchored to the date range trigger");
        }
    }

    [Fact]
    public async Task Batch_Sync_Status_Renders_Not_Attempted_As_A_Separate_Counter()
    {
        var (factory, client) = await CreateAdminAsync();
        using (factory)
        {
            var html = await (await client.GetAsync("/Admin/TikTok")).Content.ReadAsStringAsync();
            Assert.Contains("id=\"bsNotAttempted\"", html);
            Assert.Contains("Unclassified", html);
        }
    }
}
