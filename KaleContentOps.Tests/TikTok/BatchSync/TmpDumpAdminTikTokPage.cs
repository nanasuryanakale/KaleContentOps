using System;
using System.IO;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.Tmp;

/// <summary>Temporary helper: dumps the authenticated /Admin/TikTok markup for browser verification.</summary>
public class TmpDumpAdminTikTokPage
{
    [Fact]
    public async Task Dump_Admin_TikTok_Html()
    {
        using var factory = new AuthTestFactory();
        var username = "layout." + Guid.NewGuid().ToString("N")[..8];
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Administrator);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 1; i <= 3; i++)
            {
                db.TikTokShops.Add(new TikTokShop
                {
                    ShopCipher = "SHOP-DEMO-" + i,
                    ShopName = "Toko Demo " + i,
                    ShopId = "S" + i,
                    ShopCode = "SC" + i
                });
            }
            await db.SaveChangesAsync();
        }

        using var client = await factory.SignInAsync(username);
        var response = await client.GetAsync("/Admin/TikTok");
        var html = await response.Content.ReadAsStringAsync();

        var dir = Path.Combine(Path.GetTempPath(), "admin-tiktok-layout");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "index.html");
        await File.WriteAllTextAsync(path, html);
        Console.WriteLine("DUMP_PATH=" + path);
        Assert.Contains("batchSyncForm", html);
        Assert.Contains("Toko Demo 1", html);
    }
}
