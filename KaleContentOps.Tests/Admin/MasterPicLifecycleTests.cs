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
using KaleContentOps.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.Admin;

/// <summary>
/// Phase 2B acceptance tests - MASTER PIC lifecycle management (UI/API).
/// Everything runs through the real MVC pipeline on the EF InMemory test store
/// (AuthTestFactory), so no development/production data is touched.
///
/// Covered: Index page + sidebar menu + empty state, Create / CreateAjax defaults,
/// explicit JoinDate, future-JoinDate rejection, Edit validation (format, required,
/// ordering, name uniqueness incl. own-name retention), resignation deactivation
/// (business date from IShopTimeZone, never UpdatedAt/CreatedAt), safe reactivation,
/// blocked reopening of a closed employment period, ContentLog compatibility and
/// anonymous authorization behaviour.
/// </summary>
public class MasterPicLifecycleTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<AuthTestFactory> CreateReadyAsync(string username)
    {
        var factory = new AuthTestFactory();
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        return factory;
    }

    /// <summary>Application business date (Asia/Jakarta) - the lifecycle source of truth.</summary>
    private static DateOnly AppToday(AuthTestFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IShopTimeZone>().Today();
    }

    private static async Task<int> SeedPicAsync(
        AuthTestFactory factory,
        string name,
        bool isActive = true,
        DateOnly? joinDate = null,
        DateOnly? resignDate = null,
        DateTime? createdAt = null,
        DateTime? updatedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pic = new MasterPic
        {
            Name = name,
            IsActive = isActive,
            JoinDate = joinDate ?? AppToday(factory),
            ResignDate = resignDate
        };
        if (createdAt.HasValue) pic.CreatedAt = createdAt.Value;
        if (updatedAt.HasValue) pic.UpdatedAt = updatedAt.Value;

        db.MasterPics.Add(pic);
        await db.SaveChangesAsync();
        return pic.Id;
    }

    private static async Task<MasterPic> GetPicAsync(AuthTestFactory factory, int id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MasterPics.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static async Task<List<MasterPic>> GetAllPicsAsync(AuthTestFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MasterPics.AsNoTracking().ToListAsync();
    }

    private static async Task<int> CountPicsAsync(AuthTestFactory factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MasterPics.CountAsync(x => x.Name == name);
    }

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string url,
        IDictionary<string, string> fields)
        => client.PostAsync(url, new FormUrlEncodedContent(fields));

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/MasterPic");
        return AuthTestFactory.ExtractAntiforgeryToken(html)
            ?? throw new InvalidOperationException("Antiforgery token not found on /MasterPic");
    }

    private static Dictionary<string, string> EditFields(
        int id,
        string name,
        string joinDate,
        string resignDate = "",
        string? token = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["id"] = id.ToString(),
            ["name"] = name,
            ["joinDate"] = joinDate,
            ["resignDate"] = resignDate
        };
        if (!string.IsNullOrEmpty(token))
            fields["__RequestVerificationToken"] = token!;
        return fields;
    }

    // ==================================================================
    // 17/18/19 - page rendering, menu, empty state, authorization
    // ==================================================================

    [Fact]
    public async Task Index_Renders_ManagementPage_WithScopedCss_And_SidebarMenu()
    {
        const string user = "mp2b.render";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            await SeedPicAsync(factory, "PIC Render 2B"); // ensure the table (not the empty state) renders

            var client = await factory.SignInAsync(user);
            var response = await client.GetAsync("/MasterPic");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();

            Assert.Contains("Master PIC", html);
            Assert.Contains("master-pic-page", html);
            Assert.Contains("css/master-pic.css", html);   // dedicated scoped stylesheet
            Assert.Contains("js/master-pic.js", html);
            Assert.Contains("id=\"mpTable\"", html);
            // dedicated sidebar menu item, active on this page
            Assert.Contains("class=\"nav-link active\" href=\"/MasterPic\"", html);
            // the create form defaults to the Asia/Jakarta business date (never the browser clock)
            Assert.Contains($"max=\"{AppToday(factory):yyyy-MM-dd}\"", html);
        }
    }

    [Fact]
    public async Task Index_Shows_EmptyState_When_No_Pic_Exists()
    {
        const string user = "mp2b.empty";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync("/MasterPic");

            Assert.Contains("id=\"mpEmptyState\"", html);
            Assert.Contains("Belum ada Master PIC", html);
        }
    }

    [Fact]
    public async Task Index_Shows_Both_Active_And_Inactive_Pics()
    {
        const string user = "mp2b.list";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            await SeedPicAsync(factory, "PIC Aktif 2B");
            await SeedPicAsync(
                factory,
                "PIC Alm 2B",
                isActive: false,
                joinDate: new DateOnly(2025, 1, 1),
                resignDate: new DateOnly(2026, 5, 31));

            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync("/MasterPic");

            Assert.Contains("PIC Aktif 2B", html);
            Assert.Contains("PIC Alm 2B", html);          // historical visibility
            Assert.Contains("mp-pill-active", html);
            Assert.Contains("mp-pill-inactive", html);
            Assert.Contains("31-05-2026", html);          // displayed ResignDate
            Assert.Contains("01-01-2025", html);          // displayed JoinDate
        }
    }

    [Fact]
    public async Task Anonymous_Request_Is_Redirected_To_Login()
    {
        var factory = new AuthTestFactory();
        using (factory)
        {
            var client = factory.CreateBrowserClient(allowAutoRedirect: false);

            var get = await client.GetAsync("/MasterPic");
            Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
            Assert.Contains("/Account/Login", get.Headers.Location?.ToString() ?? string.Empty);

            var post = await PostFormAsync(client, "/MasterPic/Edit",
                EditFields(1, "Hantu", "2025-01-01"));
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            Assert.Contains("/Account/Login", post.Headers.Location?.ToString() ?? string.Empty);
        }
    }

    // ==================================================================
    // 1/3/4 - Create
    // ==================================================================

    [Fact]
    public async Task Create_Without_JoinDate_Defaults_To_BusinessDate_With_Null_ResignDate()
    {
        const string user = "mp2b.create";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var name = $"PIC-{Guid.NewGuid():N}"[..12];
            var expectedJoinDate = AppToday(factory);

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var response = await PostFormAsync(client,
                $"/MasterPic/Create?name={Uri.EscapeDataString(name)}",
                new Dictionary<string, string>());

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var pic = (await GetAllPicsAsync(factory)).Single(x => x.Name == name);
            Assert.Equal(expectedJoinDate, pic.JoinDate);
            Assert.Null(pic.ResignDate);
            Assert.True(pic.IsActive);
            Assert.NotEqual(default(DateOnly), pic.JoinDate);
        }
    }

    [Fact]
    public async Task Create_With_Explicit_JoinDate_Persists_Exact_Value()
    {
        const string user = "mp2b.create.explicit";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var name = $"PIC-{Guid.NewGuid():N}"[..12];
            var client = await factory.SignInAsync(user, allowAutoRedirect: false);

            var response = await PostFormAsync(client, "/MasterPic/Create", new Dictionary<string, string>
            {
                ["name"] = name,
                ["joinDate"] = "2025-03-15"
            });

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var pic = (await GetAllPicsAsync(factory)).Single(x => x.Name == name);
            Assert.Equal(new DateOnly(2025, 3, 15), pic.JoinDate);
            Assert.Null(pic.ResignDate);
            Assert.True(pic.IsActive);
        }
    }

    [Fact]
    public async Task Create_Rejects_Future_JoinDate()
    {
        const string user = "mp2b.create.future";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var name = $"PIC-{Guid.NewGuid():N}"[..12];
            var client = await factory.SignInAsync(user, allowAutoRedirect: false);

            var response = await PostFormAsync(client, "/MasterPic/Create", new Dictionary<string, string>
            {
                ["name"] = name,
                ["joinDate"] = AppToday(factory).AddDays(1).ToString("yyyy-MM-dd")
            });

            // invalid input re-renders the page with validation feedback (no silent repair)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("JoinDate tidak boleh melebihi tanggal bisnis hari ini.", html);

            Assert.Equal(0, await CountPicsAsync(factory, name));
        }
    }

    [Fact]
    public async Task Create_Rejects_Invalid_And_Missing_JoinDate_With_Feedback()
    {
        const string user = "mp2b.create.invalid";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var client = await factory.SignInAsync(user, allowAutoRedirect: false);

            var badFormat = await PostFormAsync(client, "/MasterPic/Create", new Dictionary<string, string>
            {
                ["name"] = $"PIC-BAD-{Guid.NewGuid():N}"[..12],
                ["joinDate"] = "31/12/2025"
            });
            Assert.Equal(HttpStatusCode.OK, badFormat.StatusCode);
            Assert.Contains("JoinDate tidak valid. Gunakan format YYYY-MM-DD.",
                await badFormat.Content.ReadAsStringAsync());

            var emptyName = await PostFormAsync(client, "/MasterPic/Create",
                new Dictionary<string, string> { ["name"] = "   " });
            Assert.Equal(HttpStatusCode.OK, emptyName.StatusCode);
            Assert.Contains("Nama PIC wajib diisi.", await emptyName.Content.ReadAsStringAsync());
        }
    }

    // ==================================================================
    // 2 - CreateAjax (Content Log quick-add) compatibility
    // ==================================================================

    [Fact]
    public async Task CreateAjax_Defaults_JoinDate_And_Leaves_ResignDate_Null()
    {
        const string user = "mp2b.ajax";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var name = $"PIC-{Guid.NewGuid():N}"[..12];
            var expectedJoinDate = AppToday(factory);

            var client = await factory.SignInAsync(user);
            var body = new StringContent(
                JsonSerializer.Serialize(new { name }),
                Encoding.UTF8,
                "application/json");
            var response = await client.PostAsync("/MasterPic/CreateAjax", body);

            Assert.True(response.IsSuccessStatusCode,
                $"CreateAjax failed: {response.StatusCode}");

            var pic = (await GetAllPicsAsync(factory)).Single(x => x.Name == name);
            Assert.Equal(expectedJoinDate, pic.JoinDate);
            Assert.Null(pic.ResignDate);
            Assert.True(pic.IsActive);
        }
    }

    [Fact]
    public async Task CreateAjax_Duplicate_Name_Still_Returns_Conflict()
    {
        const string user = "mp2b.ajax.dup";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var name = $"PIC-{Guid.NewGuid():N}"[..12];
            var client = await factory.SignInAsync(user);

            async Task<HttpResponseMessage> CreateAjaxAsync()
            {
                var body = new StringContent(
                    JsonSerializer.Serialize(new { name }),
                    Encoding.UTF8,
                    "application/json");
                return await client.PostAsync("/MasterPic/CreateAjax", body);
            }

            var first = await CreateAjaxAsync();
            var second = await CreateAjaxAsync();

            Assert.True(first.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal(1, await CountPicsAsync(factory, name));
        }
    }

    [Fact]
    public async Task Create_Rejects_Duplicate_Name_Through_Form()
    {
        const string user = "mp2b.create.dup";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var name = $"PIC-{Guid.NewGuid():N}"[..12];
            var client = await factory.SignInAsync(user, allowAutoRedirect: false);

            var first = await PostFormAsync(client, "/MasterPic/Create",
                new Dictionary<string, string> { ["name"] = name });
            Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

            var second = await PostFormAsync(client, "/MasterPic/Create",
                new Dictionary<string, string> { ["name"] = name });
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Contains("Nama PIC sudah digunakan oleh PIC lain.",
                await second.Content.ReadAsStringAsync());

            Assert.Equal(1, await CountPicsAsync(factory, name));
        }
    }

    // ==================================================================
    // 4/5/6/7/8/13/14 - Edit + validation
    // ==================================================================

    [Fact]
    public async Task Edit_Persists_Valid_JoinDate_And_ResignDate()
    {
        const string user = "mp2b.edit.ok";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var id = await SeedPicAsync(factory, "PIC Edit OK",
                joinDate: new DateOnly(2025, 1, 1));

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var token = await GetAntiforgeryTokenAsync(client);

            var response = await PostFormAsync(client, "/MasterPic/Edit",
                EditFields(id, "PIC Edit OK", "2025-02-01", "2025-06-30", token));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var pic = await GetPicAsync(factory, id);
            Assert.Equal(new DateOnly(2025, 2, 1), pic.JoinDate);
            Assert.Equal(new DateOnly(2025, 6, 30), pic.ResignDate);
        }
    }

    [Fact]
    public async Task Edit_Accepts_Null_ResignDate_And_Retains_Existing_Name()
    {
        const string user = "mp2b.edit.null";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var id = await SeedPicAsync(factory, "PIC Nama Tetap",
                joinDate: new DateOnly(2025, 1, 1));

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var token = await GetAntiforgeryTokenAsync(client);

            // same name (own id excluded from the duplicate check) + blank ResignDate
            var response = await PostFormAsync(client, "/MasterPic/Edit",
                EditFields(id, "PIC Nama Tetap", "2025-01-15", "", token));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var pic = await GetPicAsync(factory, id);
            Assert.Equal("PIC Nama Tetap", pic.Name);
            Assert.Equal(new DateOnly(2025, 1, 15), pic.JoinDate);
            Assert.Null(pic.ResignDate);
        }
    }

    [Fact]
    public async Task Edit_Rejects_ResignDate_Before_JoinDate()
    {
        const string user = "mp2b.edit.order";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var id = await SeedPicAsync(factory, "PIC Order Bad",
                joinDate: new DateOnly(2025, 1, 1));

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var token = await GetAntiforgeryTokenAsync(client);

            var response = await PostFormAsync(client, "/MasterPic/Edit",
                EditFields(id, "PIC Order Bad", "2025-01-01", "2024-12-31", token));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("ResignDate tidak boleh lebih awal dari JoinDate.", html);

            var pic = await GetPicAsync(factory, id); // unchanged - no silent repair
            Assert.Equal(new DateOnly(2025, 1, 1), pic.JoinDate);
            Assert.Null(pic.ResignDate);
        }
    }

    [Fact]
    public async Task Edit_Rejects_Future_JoinDate()
    {
        const string user = "mp2b.edit.future";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var id = await SeedPicAsync(factory, "PIC Future Bad",
                joinDate: new DateOnly(2025, 1, 1));

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var token = await GetAntiforgeryTokenAsync(client);

            var response = await PostFormAsync(client, "/MasterPic/Edit",
                EditFields(id, "PIC Future Bad", AppToday(factory).AddDays(2).ToString("yyyy-MM-dd"), "", token));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("JoinDate tidak boleh melebihi tanggal bisnis hari ini.",
                await response.Content.ReadAsStringAsync());

            var pic = await GetPicAsync(factory, id);
            Assert.Equal(new DateOnly(2025, 1, 1), pic.JoinDate);
        }
    }

    [Fact]
    public async Task Edit_Rejects_Missing_JoinDate_And_Invalid_Format()
    {
        const string user = "mp2b.edit.required";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var id = await SeedPicAsync(factory, "PIC Required Bad");

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var token = await GetAntiforgeryTokenAsync(client);

            var missing = await PostFormAsync(client, "/MasterPic/Edit",
                EditFields(id, "PIC Required Bad", "", "", token));
            Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
            Assert.Contains("JoinDate wajib diisi.", await missing.Content.ReadAsStringAsync());

            var badFormat = await PostFormAsync(client, "/MasterPic/Edit",
                EditFields(id, "PIC Required Bad", "2025-13-45", "", token));
            Assert.Equal(HttpStatusCode.OK, badFormat.StatusCode);
            Assert.Contains("JoinDate tidak valid. Gunakan format YYYY-MM-DD.",
                await badFormat.Content.ReadAsStringAsync());

            var emptyName = await PostFormAsync(client, "/MasterPic/Edit",
                EditFields(id, "  ", AppToday(factory).ToString("yyyy-MM-dd"), "", token));
            Assert.Equal(HttpStatusCode.OK, emptyName.StatusCode);
            Assert.Contains("Nama PIC wajib diisi.", await emptyName.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Edit_Rejects_Name_Used_By_Another_Pic()
    {
        const string user = "mp2b.edit.dup";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var taken = await SeedPicAsync(factory, "PIC Taken");
            var mine = await SeedPicAsync(factory, "PIC Mine");

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var token = await GetAntiforgeryTokenAsync(client);

            var response = await PostFormAsync(client, "/MasterPic/Edit",
                EditFields(mine, "PIC Taken", AppToday(factory).ToString("yyyy-MM-dd"), "", token));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Nama PIC sudah digunakan oleh PIC lain.",
                await response.Content.ReadAsStringAsync());

            var pic = await GetPicAsync(factory, mine);
            Assert.Equal("PIC Mine", pic.Name);
            Assert.True(taken > 0);
        }
    }

    // ==================================================================
    // 9/10 - deactivation / resignation
    // ==================================================================

    [Fact]
    public async Task Deactivate_Sets_Inactive_And_ResignDate_To_AsiaJakarta_BusinessDate()
    {
        const string user = "mp2b.deactivate";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var oldStamp = DateTime.UtcNow.AddDays(-30);
            var id = await SeedPicAsync(
                factory,
                "PIC Resign 2B",
                isActive: true,
                joinDate: new DateOnly(2025, 1, 1),
                createdAt: oldStamp,
                updatedAt: oldStamp);
            var businessToday = AppToday(factory);

            var client = await factory.SignInAsync(user);
            var response = await PostFormAsync(client, $"/MasterPic/Deactivate?id={id}",
                new Dictionary<string, string>());

            Assert.True(response.IsSuccessStatusCode,
                $"Deactivate failed: {response.StatusCode}");

            var pic = await GetPicAsync(factory, id);
            Assert.False(pic.IsActive);
            Assert.Equal(businessToday, pic.ResignDate);

            // UpdatedAt/CreatedAt are maintenance timestamps, never the ResignDate source.
            Assert.NotEqual(DateOnly.FromDateTime(oldStamp), pic.ResignDate);  // not CreatedAt / old UpdatedAt
            Assert.True((DateTime.UtcNow - pic.UpdatedAt).Duration() < TimeSpan.FromMinutes(5),
                $"UpdatedAt was not refreshed as a maintenance timestamp: {pic.UpdatedAt:O}");
            Assert.Equal(new DateOnly(2025, 1, 1), pic.JoinDate);              // JoinDate untouched
        }
    }

    [Fact]
    public async Task Deactivate_Is_Safe_For_Already_Inactive_Pic_And_Keeps_Recorded_ResignDate()
    {
        const string user = "mp2b.deactivate.idem";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var id = await SeedPicAsync(
                factory,
                "PIC Admin Off",
                isActive: false,
                joinDate: new DateOnly(2025, 1, 1),
                resignDate: null);

            var client = await factory.SignInAsync(user);
            var response = await PostFormAsync(client, $"/MasterPic/Deactivate?id={id}",
                new Dictionary<string, string>());
            Assert.True(response.IsSuccessStatusCode);

            // Administrative deactivation without a known resignation date stays NULL -
            // no date is fabricated for an already inactive PIC.
            var pic = await GetPicAsync(factory, id);
            Assert.False(pic.IsActive);
            Assert.Null(pic.ResignDate);
        }
    }

    [Fact]
    public async Task DeactivateAjax_Closes_Period_And_Preserves_ContentLog_Pic_Reference()
    {
        const string user = "mp2b.deactivate.ajax";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var id = await SeedPicAsync(factory, "PIC Ajax Off");
            long logId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = new ContentLog
                {
                    VideoId = $"mp2b-{Guid.NewGuid():N}"[..20],
                    VideoPostTime = new DateTime(2026, 9, 3, 10, 0, 0),
                    PicId = id
                };
                db.ContentLogs.Add(log);
                await db.SaveChangesAsync();
                logId = log.Id;
            }

            var client = await factory.SignInAsync(user);
            var body = new StringContent(JsonSerializer.Serialize(new { id }),
                Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/MasterPic/DeactivateAjax", body);
            Assert.True(response.IsSuccessStatusCode);

            var pic = await GetPicAsync(factory, id);
            Assert.False(pic.IsActive);
            Assert.Equal(AppToday(factory), pic.ResignDate);

            // ContentLog.PicId is untouched - the historical assignment still resolves.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.ContentLogs.AsNoTracking().SingleAsync(x => x.Id == logId);
                Assert.Equal(id, log.PicId);
            }
        }
    }

    // ==================================================================
    // 11/12 - safe reactivation
    // ==================================================================

    [Fact]
    public async Task Toggle_Reactivates_Inactive_Pic_With_Null_ResignDate()
    {
        const string user = "mp2b.reactivate";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var id = await SeedPicAsync(
                factory,
                "PIC Back",
                isActive: false,
                joinDate: new DateOnly(2025, 1, 1),
                resignDate: null);

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var response = await PostFormAsync(client, $"/MasterPic/Toggle?id={id}",
                new Dictionary<string, string>());

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var pic = await GetPicAsync(factory, id);
            Assert.True(pic.IsActive);
            Assert.Null(pic.ResignDate);           // stays NULL, never backfilled
            Assert.Equal(new DateOnly(2025, 1, 1), pic.JoinDate); // never overwritten
        }
    }

    [Fact]
    public async Task Toggle_Blocks_Reactivation_When_Employment_Period_Is_Closed()
    {
        const string user = "mp2b.reactivate.blocked";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var id = await SeedPicAsync(
                factory,
                "PIC Closed",
                isActive: false,
                joinDate: new DateOnly(2025, 1, 1),
                resignDate: new DateOnly(2026, 5, 31));

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var response = await PostFormAsync(client, $"/MasterPic/Toggle?id={id}",
                new Dictionary<string, string>());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var payload = await response.Content.ReadAsStringAsync();
            Assert.Contains("belum mendukung lebih dari satu periode kerja", payload);

            var pic = await GetPicAsync(factory, id);
            Assert.False(pic.IsActive);              // not silently reactivated
            Assert.Equal(new DateOnly(2026, 5, 31), pic.ResignDate); // closed period preserved
            Assert.Equal(new DateOnly(2025, 1, 1), pic.JoinDate);    // JoinDate untouched
        }
    }

    // ==================================================================
    // 15/16 - ContentLog compatibility
    // ==================================================================

    [Fact]
    public async Task ContentLog_Shows_Active_Pic_And_Historical_Inactive_Pic()
    {
        const string user = "mp2b.contentlog";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var activeId = await SeedPicAsync(factory, "PIC CL Aktif");
            var historicId = await SeedPicAsync(
                factory,
                "PIC CL Alm",
                isActive: false,
                joinDate: new DateOnly(2025, 1, 1),
                resignDate: new DateOnly(2026, 3, 31));

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.ContentLogs.Add(new ContentLog
                {
                    VideoId = $"mp2b-a-{Guid.NewGuid():N}"[..20],
                    VideoPostTime = new DateTime(2026, 9, 3, 10, 0, 0),
                    PicId = activeId
                });
                db.ContentLogs.Add(new ContentLog
                {
                    VideoId = $"mp2b-h-{Guid.NewGuid():N}"[..20],
                    VideoPostTime = new DateTime(2026, 9, 3, 11, 0, 0),
                    PicId = historicId
                });
                await db.SaveChangesAsync();
            }

            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync("/ContentLog");

            Assert.Contains("PIC CL Aktif", html);                       // active PIC assignable
            Assert.Contains("PIC CL Alm (dihapus dari master)", html);   // historical display intact

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.ContentLogs.AsNoTracking()
                    .Where(x => x.PicId == historicId)
                    .SingleAsync();
                Assert.Equal(historicId, log.PicId);                      // FK data untouched
            }
        }
    }
}
