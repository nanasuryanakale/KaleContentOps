using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.Admin;

/// <summary>
/// Phase 2A acceptance tests: MASTER PIC employment period (JoinDate / ResignDate).
/// Verifies, through the real MVC pipeline, that after JoinDate became a required
/// `date` column:
/// - the existing Create flow still succeeds and stamps a real business JoinDate,
/// - the existing CreateAjax flow (Content Log inline PIC creation) still succeeds,
/// - ResignDate is NOT set on creation (no fabricated resignation),
/// - JoinDate comes from the shop timezone (Asia/Jakarta business date), never a
///   raw UTC/empty/0001-01-01 value.
/// Uses the established AuthTestFactory (EF InMemory) so no development data is mutated.
/// </summary>
public class MasterPicEmploymentPeriodTests
{
    private static async Task<AuthTestFactory> CreateReadyAsync(string username)
    {
        var factory = new AuthTestFactory();
        await factory.CreateRoleUserAsync(username, "Viewer");
        return factory;
    }

    private static DateOnly AppToday(AuthTestFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IShopTimeZone>().Today();
    }

    [Fact]
    public async Task Create_Sets_JoinDate_To_ShopBusinessDate_And_Leaves_ResignDate_Null()
    {
        const string user = "pic2a.create";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var name = $"PIC-{Guid.NewGuid():N}"[..12];
            var expectedJoinDate = AppToday(factory);

            var client = await factory.SignInAsync(user, allowAutoRedirect: false);
            var response = await client.PostAsync(
                $"/MasterPic/Create?name={Uri.EscapeDataString(name)}",
                new StringContent(string.Empty));

            // Create redirects to Index (no MasterPic view exists yet - Phase 2B owns
            // that UI), so the redirect must be followed for the INSERT, not the view.
            Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pic = db.MasterPics.Single(x => x.Name == name);

            Assert.Equal(expectedJoinDate, pic.JoinDate);
            Assert.Null(pic.ResignDate);          // never derived from UpdatedAt/CreatedAt/today
            Assert.NotEqual(default(DateOnly), pic.JoinDate); // never 0001-01-01
            Assert.True(pic.IsActive);
        }
    }

    [Fact]
    public async Task CreateAjax_Sets_JoinDate_And_Leaves_ResignDate_Null()
    {
        const string user = "pic2a.ajax";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var name = $"PIC-{Guid.NewGuid():N}"[..12];
            var expectedJoinDate = AppToday(factory);

            var client = await factory.SignInAsync(user);
            using var body = new StringContent(
                JsonSerializer.Serialize(new { name }),
                System.Text.Encoding.UTF8,
                "application/json");
            var response = await client.PostAsync("/MasterPic/CreateAjax", body);

            Assert.True(response.IsSuccessStatusCode,
                $"CreateAjax failed: {response.StatusCode}");

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pic = db.MasterPics.Single(x => x.Name == name);

            Assert.Equal(expectedJoinDate, pic.JoinDate);
            Assert.Null(pic.ResignDate);
            Assert.NotEqual(default(DateOnly), pic.JoinDate);
        }
    }

    [Fact]
    public async Task Deactivate_Closes_Period_With_BusinessDate_Not_UpdatedAt()
    {
        // PHASE 2B SEMANTIC UPDATE: in Phase 2A ResignDate stayed NULL because the
        // lifecycle rules were explicitly deferred ("Phase 2B owns that lifecycle").
        // Phase 2B Step 6 now closes the employment period on explicit deactivation
        // with the Asia/Jakarta business date, and this test additionally proves that
        // neither UpdatedAt nor CreatedAt was used as the ResignDate source.
        const string user = "pic2a.deact";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var oldStamp = DateTime.UtcNow.AddDays(-30);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.MasterPics.Add(new KaleContentOps.Models.MasterPic
                {
                    Name = $"PIC-{Guid.NewGuid():N}"[..12],
                    IsActive = true,
                    JoinDate = AppToday(factory),
                    ResignDate = null,
                    CreatedAt = oldStamp,
                    UpdatedAt = oldStamp
                });
                db.SaveChanges();
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var id = db.MasterPics.OrderByDescending(x => x.Id).First().Id;

                var client = await factory.SignInAsync(user);
                var response = await client.PostAsync($"/MasterPic/Deactivate?id={id}", new StringContent(string.Empty));
                response.EnsureSuccessStatusCode();

                // AsNoTracking: the request handler already tracked this row in another
                // context; verification must read the persisted state, not a tracked copy.
                var pic = db.MasterPics.AsNoTracking().Single(x => x.Id == id);
                Assert.False(pic.IsActive);                       // existing soft-deactivate behaviour preserved
                Assert.Equal(AppToday(factory), pic.ResignDate); // business date (Asia/Jakarta), Phase 2B
                Assert.NotEqual(DateOnly.FromDateTime(oldStamp), pic.ResignDate); // not CreatedAt / old UpdatedAt
                Assert.True((DateTime.UtcNow - pic.UpdatedAt).Duration() < TimeSpan.FromMinutes(5),
                    $"UpdatedAt must remain a maintenance timestamp: {pic.UpdatedAt:O}");
            }
        }
    }
}
