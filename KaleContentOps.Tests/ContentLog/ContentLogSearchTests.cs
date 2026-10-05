using System;
using System.Linq;
using System.Threading.Tasks;
using KaleContentOps.Controllers;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace KaleContentOps.Tests.ContentLogFiltering;

/// <summary>
/// Regression coverage for the Content Log search bar (server-side LINQ only).
/// The search must cover the data represented by the table: Title, VideoId, Username,
/// CreatorNickname, Content Type, Production Method, PIC, Views and VideoPostTime dates.
/// Date-only search ("2026-10-01") matches the Jakarta calendar day because VideoPostTime
/// is stored as a naive shop-local (Asia/Jakarta, GMT+7) timestamp:
///   startInclusive &lt;= VideoPostTime &lt; startNextDay.
/// Pure in-memory queries: no TikTok API, no sync, no database writes.
/// </summary>
public class ContentLogSearchTests
{
    private const string TitleAlpha = "Judul Alpha Sakura";
    private const string TitleBeta = "Judul Beta Melati";
    private const string TitleGamma = "Judul Gamma Cempaka";

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IShopTimeZone ShopTz() =>
        new ShopTimeZone(Options.Create(new ShopTimeZoneOptions()));

    private static async Task<AppDbContext> SeedAsync(AppDbContext db)
    {
        db.ContentLogs.AddRange(
            // Title / VideoId / Username search fixtures
            new ContentLog { VideoId = "7631544267172842773", Title = TitleAlpha, Username = "kaleofficial", CreatorNickname = "Kale Official", VideoPostTime = new DateTime(2026, 9, 28, 10, 0, 0) },
            new ContentLog { VideoId = "vid-beta", Title = TitleBeta, Username = "othershop", CreatorNickname = "Other Nick", VideoPostTime = new DateTime(2026, 9, 29, 11, 0, 0) },
            // Jakarta calendar-day boundary fixtures for 2026-10-01 (shop-local naive storage)
            new ContentLog { VideoId = "oct-last-second", Title = TitleGamma, Username = "kaleofficial", VideoPostTime = new DateTime(2026, 10, 1, 23, 59, 59) },
            new ContentLog { VideoId = "sep-last-second", Title = "Sehari sebelum", Username = "kaleofficial", VideoPostTime = new DateTime(2026, 9, 30, 23, 59, 59) },
            new ContentLog { VideoId = "oct-first-instant", Title = "Awal berikutnya", Username = "kaleofficial", VideoPostTime = new DateTime(2026, 10, 2, 0, 0, 0) },
            new ContentLog { VideoId = "no-date", Title = "Tanpa tanggal", Username = "kaleofficial", VideoPostTime = null });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<ContentLogIndexViewModel> QueryAsync(
        AppDbContext db,
        string? search = null,
        string? contentType = null,
        int page = 1,
        int pageSize = 20)
    {
        var controller = new ContentLogController(db, ShopTz());
        var result = await controller.Index(search, contentType, null, null, page, pageSize);
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<ContentLogIndexViewModel>(view.Model);
    }

    private static string[] Titles(ContentLogIndexViewModel vm) =>
        vm.Items.Select(i => i.Title ?? string.Empty).ToArray();

    [Fact]
    public async Task Search_By_Title_Finds_Matching_Rows()
    {
        var db = await SeedAsync(CreateDb());

        var vm = await QueryAsync(db, search: "Sakura");

        Assert.Equal(1, vm.TotalItems);
        Assert.Equal(TitleAlpha, vm.Items[0].Title);
        Assert.Equal("Sakura", vm.Search);
    }

    [Fact]
    public async Task Search_By_VideoId_Finds_Matching_Rows()
    {
        var db = await SeedAsync(CreateDb());

        var vm = await QueryAsync(db, search: "7631544267172842773");

        Assert.Equal(1, vm.TotalItems);
        Assert.Equal(TitleAlpha, vm.Items[0].Title);

        var partial = await QueryAsync(db, search: "vid-beta");
        Assert.Equal(1, partial.TotalItems);
        Assert.Equal(TitleBeta, partial.Items[0].Title);
    }

    [Fact]
    public async Task Search_By_Username_Finds_Matching_Rows()
    {
        var db = await SeedAsync(CreateDb());

        var vm = await QueryAsync(db, search: "othershop");

        Assert.Equal(1, vm.TotalItems);
        Assert.Equal(TitleBeta, vm.Items[0].Title);
    }

    [Fact]
    public async Task Search_By_CreatorNickname_Finds_Matching_Rows()
    {
        var db = await SeedAsync(CreateDb());

        var vm = await QueryAsync(db, search: "Other Nick");

        Assert.Equal(1, vm.TotalItems);
        Assert.Equal(TitleBeta, vm.Items[0].Title);
    }

    [Fact]
    public async Task Search_By_IsoDate_Matches_Jakarta_Calendar_Day()
    {
        var db = await SeedAsync(CreateDb());

        // The exact string the spec requires: 2026-10-01.
        var vm = await QueryAsync(db, search: "2026-10-01");

        // Whole shop-local day: 00:00:00 .. 23:59:59 all in, neighbours out.
        Assert.Equal(1, vm.TotalItems);
        Assert.Equal(TitleGamma, vm.Items[0].Title);

        var titles = Titles(vm);
        Assert.DoesNotContain("Sehari sebelum", titles);   // 2026-09-30 23:59:59
        Assert.DoesNotContain("Awal berikutnya", titles);  // 2026-10-02 00:00:00
        Assert.DoesNotContain("Tanpa tanggal", titles);    // NULL VideoPostTime
    }

    [Fact]
    public async Task Search_By_Jakarta_Day_Is_HalfOpen_And_Tz_Consistent()
    {
        var tz = ShopTz();
        // Boundary = shop-local (Asia/Jakarta) midnight, never a raw UTC conversion.
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0), tz.ToDateTime(new DateOnly(2026, 10, 1)));
        Assert.Equal(new DateTime(2026, 10, 2, 0, 0, 0), tz.ToDateTime(new DateOnly(2026, 10, 1).AddDays(1)));

        var db = await SeedAsync(CreateDb());

        // Displayed dd/MM/yyyy format still resolves to the same Jakarta day.
        var displayed = await QueryAsync(db, search: "01/10/2026");
        Assert.Equal(1, displayed.TotalItems);
        Assert.Equal(TitleGamma, displayed.Items[0].Title);
    }

    [Fact]
    public async Task Search_With_Pagination_Keeps_TotalCounts_And_Page_Slices()
    {
        var db = await SeedAsync(CreateDb());

        // "kaleofficial" matches 5 rows.
        var page1 = await QueryAsync(db, search: "kaleofficial", page: 1, pageSize: 2);
        Assert.Equal(5, page1.TotalItems);
        Assert.Equal(3, page1.TotalPages);
        Assert.Equal(2, page1.Items.Count);

        var page3 = await QueryAsync(db, search: "kaleofficial", page: 3, pageSize: 2);
        Assert.Equal(5, page3.TotalItems);
        Assert.Single(page3.Items);
    }

    [Fact]
    public async Task Search_Preserves_Descending_VideoPostTime_Sorting()
    {
        var db = await SeedAsync(CreateDb());

        // Matches 5 rows with staggered VideoPostTime (incl. one NULL).
        var vm = await QueryAsync(db, search: "kaleofficial", pageSize: 20);

        Assert.Equal(5, vm.TotalItems);
        var dated = vm.Items.Where(i => i.VideoPostTime != null).Select(i => i.VideoPostTime!.Value).ToList();
        Assert.Equal(dated.OrderByDescending(d => d), dated);
        // Newest first: 2026-10-02 00:00:00 is on top.
        Assert.Equal(new DateTime(2026, 10, 2, 0, 0, 0), vm.Items[0].VideoPostTime);
    }

    [Fact]
    public async Task Search_Combines_With_ContentType_Filter()
    {
        var db = CreateDb();
        db.ContentTypes.Add(new ContentType { Code = "KK", Name = "Keranjang Kuning" });
        db.ContentTypes.Add(new ContentType { Code = "NON_KK", Name = "Non-KK" });
        await db.SaveChangesAsync();
        await SeedAsync(db);

        var alpha = db.ContentLogs.Single(x => x.Title == TitleAlpha);
        alpha.ContentTypeId = db.ContentTypes.Single(c => c.Code == "KK").Id;
        var beta = db.ContentLogs.Single(x => x.Title == TitleBeta);
        beta.ContentTypeId = db.ContentTypes.Single(c => c.Code == "NON_KK").Id;
        await db.SaveChangesAsync();

        var kk = await QueryAsync(db, search: "Judul", contentType: "KK");
        Assert.Single(kk.Items);
        Assert.Equal(TitleAlpha, kk.Items[0].Title);

        var nonKk = await QueryAsync(db, search: "Judul", contentType: "NON_KK");
        Assert.Single(nonKk.Items);
        Assert.Equal(TitleBeta, nonKk.Items[0].Title);
    }
}
