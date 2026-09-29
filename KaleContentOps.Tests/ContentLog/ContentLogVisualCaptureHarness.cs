using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.ContentLogVisual;

/// <summary>
/// TEMPORARY visual-verification harness (Content Log demographics display): seeds one log
/// carrying the real VIEWERS demographics JSON (VideoId 7687996887235890452 payload) and one
/// log with NULL demographics, renders the real /ContentLog page through the MVC pipeline,
/// and captures the HTML into VisualCheck/ContentLog.html for inspection.
/// SKIPPED by default (enable with CL_VISUAL=1) so CI never depends on it.
/// </summary>
public class ContentLogVisualCaptureHarness
{
    [Fact]
    public async Task Capture()
    {
        if (Environment.GetEnvironmentVariable("CL_VISUAL") != "1")
        {
            return; // skipped by default
        }

        var factory = new AuthTestFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (!db.ContentTypes.Any(c => c.Code == "NON_KK"))
            {
                db.ContentTypes.Add(new ContentType { Code = "NON_KK", Name = "Non-KK" });
                db.SaveChanges();
            }
            var type = db.ContentTypes.Single(c => c.Code == "NON_KK");

            var withDemo = new ContentLog
            {
                VideoId = "7687996887235890452",
                Title = "Dengan demographics (VIEWERS)",
                Username = "kaleofficial",
                VideoPostTime = DateTime.UtcNow.AddDays(-1),
                ContentTypeId = type.Id
            };
            var withoutDemo = new ContentLog
            {
                VideoId = "cl-null-demo",
                Title = "Tanpa demographics (NULL state)",
                Username = "kaleofficial",
                VideoPostTime = DateTime.UtcNow.AddDays(-2),
                ContentTypeId = type.Id
            };
            db.ContentLogs.AddRange(withDemo, withoutDemo);
            db.SaveChanges();

            // Real VIEWERS payload persisted by the verified Details pipeline.
            db.ContentMetrics.Add(new ContentMetric
            {
                ContentLog = withDemo,
                Views = 477,
                Likes = 15,
                Comments = 0,
                Shares = 0,
                NewFollowers = 1,
                DemographicsJson = "{\"male\":0.684,\"female\":0.316,\"ages\":{\"35-44\":0.167,\"25-34\":0.5,\"45-54\":0.056,\"18-24\":0.278},\"countries\":{\"ID\":1.0}}",
                CapturedAt = DateTime.UtcNow
            });
            // NULL demographics: metric exists but has no demographics data.
            db.ContentMetrics.Add(new ContentMetric
            {
                ContentLog = withoutDemo,
                Views = 120,
                CapturedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        var username = "cl.capture";
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        var client = await factory.SignInAsync(username);

        var html = await client.GetStringAsync("/ContentLog");

        var dir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "VisualCheck");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "ContentLog.html"), html);

        // Populated row: gender, age and country percentages rendered from the fixture JSON.
        Assert.Contains("68.4%", html);   // male
        Assert.Contains("31.6%", html);   // female
        Assert.Contains("27.8%", html);   // 18-24
        Assert.Contains("50.0%", html);   // 25-34
        Assert.Contains("16.7%", html);   // 35-44
        Assert.Contains("5.6%", html);    // 45-54
        Assert.Contains("ID 100.0%", html); // country code + percentage

        // Country column exists and is wired into the visibility system.
        Assert.Contains("data-col=\"country\"", html);

        // NULL-state row: renders em-dash placeholders, never 0%.
        var marker = "Tanpa demographics (NULL state)";
        var nullRowIdx = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(nullRowIdx > 0, "NULL-state row not found in rendered page");
        var rowSlice = html.Substring(nullRowIdx, Math.Min(8000, html.Length - nullRowIdx));
        Assert.DoesNotContain("0.0%", rowSlice);
    }
}
