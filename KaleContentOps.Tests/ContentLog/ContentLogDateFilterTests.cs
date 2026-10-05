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
/// Regression coverage for the Content Log date / VideoPostTime filter.
/// VideoPostTime is stored as a naive shop-local (Asia/Jakarta, GMT+7) timestamp, so every
/// boundary must be a shop-local midnight and the window stays half-open:
///   startInclusive &lt;= VideoPostTime &lt; endExclusive.
/// Pure in-memory queries: no TikTok API, no sync, no database writes.
/// </summary>
public class ContentLogDateFilterTests
{
    private const string DayBefore = "Sehari sebelum rentang";
    private const string DayStart = "Tepat awal hari";
    private const string DayMid = "Tengah hari";
    private const string DayEnd = "Tepat akhir hari";
    private const string DayAfter = "Sehari sesudah rentang";
    private const string NoDate = "Tanpa VideoPostTime";

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IShopTimeZone ShopTz() =>
        new ShopTimeZone(Options.Create(new ShopTimeZoneOptions()));

    private static async Task<AppDbContext> SeedAsync(AppDbContext db)
    {
        db.ContentLogs.AddRange(
            new ContentLog { VideoId = "v-before", Title = DayBefore, VideoPostTime = new DateTime(2026, 4, 21, 12, 0, 0) },
            new ContentLog { VideoId = "v-start", Title = DayStart, VideoPostTime = new DateTime(2026, 4, 22, 0, 0, 0) },
            new ContentLog { VideoId = "v-mid", Title = DayMid, VideoPostTime = new DateTime(2026, 4, 22, 18, 29, 30) },
            new ContentLog { VideoId = "v-end", Title = DayEnd, VideoPostTime = new DateTime(2026, 4, 22, 23, 59, 59) },
            new ContentLog { VideoId = "v-after", Title = DayAfter, VideoPostTime = new DateTime(2026, 4, 23, 0, 0, 0) },
            new ContentLog { VideoId = "v-null", Title = NoDate, VideoPostTime = null });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<ContentLogIndexViewModel> QueryAsync(
        AppDbContext db,
        string? search = null,
        string? contentType = null,
        string? dateFrom = null,
        string? dateTo = null,
        int page = 1,
        int pageSize = 20)
    {
        var controller = new ContentLogController(db, ShopTz());
        var result = await controller.Index(search, contentType, dateFrom, dateTo, page, pageSize);
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<ContentLogIndexViewModel>(view.Model);
    }

    private static string[] Titles(ContentLogIndexViewModel vm) =>
        vm.Items.Select(i => i.Title ?? string.Empty).ToArray();

    [Fact]
    public async Task ExactDate_Using_Displayed_ddMMyyyy_Format_Filters_VideoPostTime()
    {
        var db = await SeedAsync(CreateDb());

        // The Tanggal column renders dd/MM/yyyy; users copy/paste exactly that.
        var vm = await QueryAsync(db, search: "22/04/2026");

        Assert.Equal(3, vm.TotalItems);
        var titles = Titles(vm);
        Assert.Contains(DayStart, titles);
        Assert.Contains(DayMid, titles);
        Assert.Contains(DayEnd, titles);
        Assert.DoesNotContain(DayBefore, titles);
        Assert.DoesNotContain(DayAfter, titles);
        Assert.DoesNotContain(NoDate, titles);
    }

    [Fact]
    public async Task ExactDate_Legacy_Dashed_Format_Still_Filters_VideoPostTime()
    {
        var db = await SeedAsync(CreateDb());

        var vm = await QueryAsync(db, search: "22-04-2026");

        Assert.Equal(3, vm.TotalItems);
        var titles = Titles(vm);
        Assert.Contains(DayStart, titles);
        Assert.Contains(DayMid, titles);
        Assert.Contains(DayEnd, titles);
        Assert.DoesNotContain(DayAfter, titles);
    }

    [Fact]
    public async Task DateRange_Is_HalfOpen_EndDayCoversWholeDay_NextDayMidnightExcluded()
    {
        var db = await SeedAsync(CreateDb());

        var vm = await QueryAsync(db, dateFrom: "2026-04-21", dateTo: "2026-04-22");

        Assert.Equal(4, vm.TotalItems);
        var titles = Titles(vm);
        Assert.Contains(DayBefore, titles);
        Assert.Contains(DayStart, titles);
        Assert.Contains(DayMid, titles);
        Assert.Contains(DayEnd, titles);          // 23:59:59 still inside the end day
        Assert.DoesNotContain(DayAfter, titles);  // 2026-04-23 00:00:00 is outside
        Assert.DoesNotContain(NoDate, titles);
    }

    [Fact]
    public async Task DateRange_Boundaries_Are_Jakarta_ShopLocal_Midnights()
    {
        var tz = ShopTz();
        Assert.Equal("Asia/Jakarta", tz.TimeZoneId);
        Assert.Equal(new DateTime(2026, 4, 22, 0, 0, 0), tz.ToDateTime(new DateOnly(2026, 4, 22)));
        Assert.Equal(new DateTime(2026, 4, 23, 0, 0, 0), tz.ToDateTime(new DateOnly(2026, 4, 22).AddDays(1)));

        var db = await SeedAsync(CreateDb());

        // Inclusive start: from shop-local midnight of the selected day onwards.
        var fromOnly = await QueryAsync(db, dateFrom: "2026-04-22");
        Assert.Equal(4, fromOnly.TotalItems);

        // Exclusive end: everything strictly before the next shop-local midnight.
        var toOnly = await QueryAsync(db, dateTo: "2026-04-22");
        Assert.Equal(4, toOnly.TotalItems);
        var titles = Titles(toOnly);
        Assert.Contains(DayEnd, titles);
        Assert.DoesNotContain(DayAfter, titles);
        Assert.DoesNotContain(NoDate, titles);
    }

    [Fact]
    public async Task TextSearch_And_DateFilter_Are_Applied_Together()
    {
        var db = await SeedAsync(CreateDb());

        var inside = await QueryAsync(db, search: DayMid, dateFrom: "2026-04-22", dateTo: "2026-04-22");
        Assert.Single(inside.Items);
        Assert.Equal(DayMid, inside.Items[0].Title);

        // Same text, day outside the range -> no rows (filters are AND-ed).
        var outside = await QueryAsync(db, search: DayMid, dateFrom: "2026-04-21", dateTo: "2026-04-21");
        Assert.Empty(outside.Items);
    }

    [Fact]
    public async Task Invalid_Date_Input_Is_Ignored_Instead_Of_Throwing()
    {
        var db = await SeedAsync(CreateDb());

        var vm = await QueryAsync(db, dateFrom: "not-a-date", dateTo: "32/13/2026");

        Assert.Equal(6, vm.TotalItems);
    }

    [Fact]
    public async Task Pagination_Keeps_DateFilter_And_TotalCounts()
    {
        var db = await SeedAsync(CreateDb());

        var page1 = await QueryAsync(db, dateFrom: "2026-04-22", dateTo: "2026-04-22", page: 1, pageSize: 2);
        Assert.Equal(3, page1.TotalItems);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(2, page1.Items.Count);

        var page2 = await QueryAsync(db, dateFrom: "2026-04-22", dateTo: "2026-04-22", page: 2, pageSize: 2);
        Assert.Equal(3, page2.TotalItems);
        Assert.Equal(2, page2.TotalPages);
        Assert.Single(page2.Items);
        Assert.Contains(page2.Items[0].Title!, new[] { DayStart, DayMid, DayEnd });
    }

    [Fact]
    public async Task ContentType_Filter_Is_Preserved_Alongside_DateFilter()
    {
        var db = CreateDb();
        db.ContentTypes.Add(new ContentType { Code = "KK", Name = "Keranjang Kuning" });
        await db.SaveChangesAsync();
        await SeedAsync(db);

        var mid = db.ContentLogs.Single(x => x.Title == DayMid);
        mid.ContentTypeId = db.ContentTypes.Single(c => c.Code == "KK").Id;
        await db.SaveChangesAsync();

        var vm = await QueryAsync(db, contentType: "KK", dateFrom: "2026-04-22", dateTo: "2026-04-22");

        Assert.Single(vm.Items);
        Assert.Equal(DayMid, vm.Items[0].Title);
        Assert.Equal("KK", vm.SelectedContentType);
    }

    [Fact]
    public async Task SearchBox_ExactDate_And_Range_RoundTrip_Through_ViewModel()
    {
        var db = await SeedAsync(CreateDb());

        var vm = await QueryAsync(db, search: "22/04/2026", dateFrom: "2026-04-01", dateTo: "2026-04-30");

        // The view model carries the selection back so pills/pagination keep it.
        Assert.Equal("22/04/2026", vm.Search);
        Assert.Equal("2026-04-01", vm.DateFrom);
        Assert.Equal("2026-04-30", vm.DateTo);
        Assert.Equal(3, vm.TotalItems);
    }
}
