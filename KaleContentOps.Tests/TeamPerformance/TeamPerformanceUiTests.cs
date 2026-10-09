using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Security;
using KaleContentOps.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KaleContentOps.Tests.TeamPerformance;

/// <summary>
/// Team Performance Razor page tests through the real MVC pipeline (AuthTestFactory):
/// route + rendering, scoped CSS asset, date-range component, empty state, PIC rows,
/// the "Belum Diisi" bucket, sidebar active state and authentication enforcement.
/// </summary>
public class TeamPerformanceUiTests
{
    private static async Task<AuthTestFactory> CreateReadyAsync(string username)
    {
        var factory = new AuthTestFactory();
        await factory.CreateRoleUserAsync(username, AuthConstants.Roles.Viewer);
        return factory;
    }

    [Fact]
    public async Task Page_Renders_Header_DateRange_And_ScopedCss()
    {
        const string user = "tp.render";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var client = await factory.SignInAsync(user);
            var html = await client.GetStringAsync("/TeamPerformance?startDate=2026-09-01&endDate=2026-09-07");

            Assert.Contains("team-performance-page", html);
            Assert.Contains("Team Performance", html);
            Assert.Contains("css/team-performance.css", html);          // dedicated scoped stylesheet
            Assert.Contains("css/calendar-popup.css", html);
            Assert.Contains("id=\"dateRangeButton\"", html);
            Assert.Contains("01/09 - 07/09 (GMT+7)", html);
            Assert.Contains("data-start=\"2026-09-01\"", html);
            Assert.Contains("data-end=\"2026-09-07\"", html);
            Assert.Contains("/TeamPerformance?startDate=", html);       // apply target
            Assert.Contains("js/calendar-popup.js", html);
        }
    }

    [Fact]
    public async Task Page_ShowsEmptyState_WhenNoContent()
    {
        const string user = "tp.empty";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var html = await (await factory.SignInAsync(user))
                .GetStringAsync("/TeamPerformance?startDate=2026-09-01&endDate=2026-09-07");

            Assert.Contains("tp-empty", html);
        }
    }

    [Fact]
    public async Task Page_ShowsPicRow_And_BelumDiisi()
    {
        const string user = "tp.rows";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pic = new MasterPic { Name = "PIC Satu", IsActive = true };
                db.MasterPics.Add(pic);
                db.SaveChanges();

                var assigned = new ContentLog
                {
                    VideoId = $"tp-{Guid.NewGuid():N}"[..20],
                    VideoPostTime = new DateTime(2026, 9, 3, 10, 0, 0),
                    PicId = pic.Id
                };
                db.ContentLogs.Add(assigned);
                db.SaveChanges();
                db.ContentMetrics.Add(new ContentMetric
                {
                    ContentLogId = assigned.Id,
                    Views = 1234,
                    CapturedAt = new DateTime(2026, 9, 4)
                });

                db.ContentLogs.Add(new ContentLog
                {
                    VideoId = $"tp-un-{Guid.NewGuid():N}"[..20],
                    VideoPostTime = new DateTime(2026, 9, 3, 11, 0, 0),
                    PicId = null
                });
                db.SaveChanges();
            }

            var html = await (await factory.SignInAsync(user))
                .GetStringAsync("/TeamPerformance?startDate=2026-09-01&endDate=2026-09-07");

            Assert.Contains("PIC Satu", html);
            Assert.Contains("tp-status-active", html);
            Assert.Contains("BELUM DIISI", html);
            // Phase 3A: employment data IS available now, so the provisional-denominator
            // notice must no longer be rendered on the page.
            Assert.DoesNotContain("tp-notice", html);
        }
    }

    [Fact]
    public async Task Sidebar_TeamPerformance_IsActive()
    {
        const string user = "tp.sidebar";
        var factory = await CreateReadyAsync(user);
        using (factory)
        {
            var html = await (await factory.SignInAsync(user)).GetStringAsync("/TeamPerformance");

            Assert.Contains("class=\"nav-link active\" href=\"/TeamPerformance\"", html);
        }
    }

    [Fact]
    public async Task Anonymous_Request_IsRedirected_To_Login()
    {
        var factory = new AuthTestFactory();
        using (factory)
        {
            var client = factory.CreateBrowserClient(allowAutoRedirect: false);
            var response = await client.GetAsync("/TeamPerformance");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
        }
    }
}
