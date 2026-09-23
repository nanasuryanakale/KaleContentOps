using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using KaleContentOps.Controllers;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.ViewModels;
using Xunit;

namespace KaleContentOps.Tests.ContentLogMapping;

public class ContentLogDemographicsMappingTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ContentLog SeedLog(AppDbContext db, string videoId = "V1")
    {
        var log = new ContentLog { VideoId = videoId, Title = "t" };
        db.ContentLogs.Add(log);
        db.SaveChanges();
        return log;
    }

    private static async Task<ContentLogListItem> GetFirstItemAsync(AppDbContext db)
    {
        var controller = new ContentLogController(db);
        var result = await controller.Index(null, null);
        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<ContentLogIndexViewModel>(view.Model);
        return Assert.Single(vm.Items);
    }

    [Fact]
    public async Task Latest_Snapshot_Demographics_Are_Mapped()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.AddRange(
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 10,
                DemographicsJson = "{\"male\":0.3,\"female\":0.7,\"ages\":{\"18-24\":0.5}}",
                CapturedAt = DateTime.UtcNow.AddMinutes(-30) // older
            },
            new ContentMetric
            {
                ContentLogId = log.Id,
                Views = 20,
                DemographicsJson = "{\"male\":0.4,\"female\":0.6,\"no_gender\":0.0,\"ages\":{\"25-34\":0.25,\"55+\":0.1}}",
                CapturedAt = DateTime.UtcNow // latest
            });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        // Latest snapshot must win
        Assert.Equal(20, item.Views);
        Assert.Equal(0.4m, item.Male);
        Assert.Equal(0.6m, item.Female);
        Assert.Equal(0.0m, item.NonGender);   // valid zero preserved
        Assert.Null(item.Age18_24);           // only in older snapshot -> null
        Assert.Equal(0.25m, item.Age25_34);
        Assert.Null(item.Age35_44);
        Assert.Null(item.Age45_54);
        Assert.Equal(0.1m, item.Age55Plus);
    }

    [Fact]
    public async Task Zero_Is_Valid_And_Not_Converted_To_Null()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 0,          // valid zero from TikTok
            Likes = 0,
            DemographicsJson = "{\"male\":0.0,\"female\":0.0,\"no_gender\":0.0,\"ages\":{\"18-24\":0.0,\"25-34\":0.0,\"35-44\":0.0,\"45-54\":0.0,\"55+\":0.0}}",
            CapturedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(0, item.Views);
        Assert.Equal(0, item.Likes);
        Assert.Equal(0.0m, item.Male);
        Assert.Equal(0.0m, item.Female);
        Assert.Equal(0.0m, item.NonGender);
        Assert.Equal(0.0m, item.Age18_24);
        Assert.Equal(0.0m, item.Age25_34);
        Assert.Equal(0.0m, item.Age35_44);
        Assert.Equal(0.0m, item.Age45_54);
        Assert.Equal(0.0m, item.Age55Plus);
    }

    [Fact]
    public async Task Missing_Demographics_Leave_Fields_Null()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 5,
            DemographicsJson = null, // metric without demographics
            CapturedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(5, item.Views);
        Assert.Null(item.DemographicsJson);
        Assert.Null(item.Male);
        Assert.Null(item.Female);
        Assert.Null(item.NonGender);
        Assert.Null(item.Age18_24);
        Assert.Null(item.Age25_34);
        Assert.Null(item.Age35_44);
        Assert.Null(item.Age45_54);
        Assert.Null(item.Age55Plus);
    }

    [Fact]
    public async Task Corrupt_Demographics_Json_Leaves_Fields_Null()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 7,
            DemographicsJson = "{not-valid-json",
            CapturedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Equal(7, item.Views);          // scalar metrics unaffected
        Assert.Null(item.Male);
        Assert.Null(item.Female);
        Assert.Null(item.Age18_24);
    }

    [Fact]
    public async Task Partial_Demographics_Map_Only_Present_Keys()
    {
        var db = CreateDb();
        var log = SeedLog(db);
        db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 9,
            DemographicsJson = "{\"ages\":{\"35-44\":0.33}}", // ages only, no gender keys
            CapturedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var item = await GetFirstItemAsync(db);

        Assert.Null(item.Male);
        Assert.Null(item.Female);
        Assert.Null(item.NonGender);
        Assert.Equal(0.33m, item.Age35_44);
        Assert.Null(item.Age18_24);
        Assert.Null(item.Age55Plus);
    }
}
