using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KaleContentOps.Tests.TikTok.BatchSync;

public class BatchSyncDetailsAndClassificationTests
{
    [Fact]
    public async Task Controlled_Preview_Selects_Only_Lowest_Pending_Without_Changing_Data()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var job = new TikTokBatchSyncJob
        {
            Id = 1,
            TikTokShopId = shop.Id,
            StartDate = new DateTime(2026, 10, 13),
            EndDate = new DateTime(2026, 10, 15),
            Status = TikTokBatchSyncJobStatus.ProcessingDetails,
            CollectionCompleted = true
        };
        ctx.Db.TikTokBatchSyncJobs.Add(job);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.TikTokBatchSyncJobItems.AddRange(
            new TikTokBatchSyncJobItem { TikTokBatchSyncJobId = 1, VideoId = "V-LATER", Status = TikTokBatchSyncJobItemStatus.Pending },
            new TikTokBatchSyncJobItem { TikTokBatchSyncJobId = 1, VideoId = "V-FIRST", Status = TikTokBatchSyncJobItemStatus.Pending },
            new TikTokBatchSyncJobItem { TikTokBatchSyncJobId = 1, VideoId = "V-DONE", Status = TikTokBatchSyncJobItemStatus.Succeeded });
        await ctx.Db.SaveChangesAsync();
        var first = await ctx.Db.TikTokBatchSyncJobItems.SingleAsync(i => i.VideoId == "V-LATER");

        var result = await ctx.Service.PreviewControlledSingleItemAsync();

        Assert.True(result.IsAvailable);
        Assert.Equal(first.Id, result.Preview!.ItemId);
        Assert.Equal("V-LATER", result.Preview.VideoId);
        Assert.Equal("2026-10-16", result.Preview.EndDateExclusive.ToString("yyyy-MM-dd"));
        Assert.Equal(TikTokBatchSyncJobItemStatus.Pending,
            await ctx.Db.TikTokBatchSyncJobItems.Where(i => i.VideoId == "V-LATER").Select(i => i.Status).SingleAsync());
        Assert.Empty(ctx.Handler.RequestLog);
    }

    [Fact]
    public async Task Controlled_Preview_Rejects_Wrong_Period_And_No_Pending_Item()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var job = new TikTokBatchSyncJob
        {
            Id = 1,
            TikTokShopId = shop.Id,
            StartDate = new DateTime(2026, 10, 12),
            EndDate = new DateTime(2026, 10, 15),
            Status = TikTokBatchSyncJobStatus.ProcessingDetails,
            CollectionCompleted = true
        };
        ctx.Db.TikTokBatchSyncJobs.Add(job);
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Service.PreviewControlledSingleItemAsync();
        Assert.False(result.IsAvailable);
        Assert.Contains("period", result.Error!, StringComparison.OrdinalIgnoreCase);

        job.StartDate = new DateTime(2026, 10, 13);
        ctx.Db.TikTokBatchSyncJobItems.Add(new TikTokBatchSyncJobItem
        {
            TikTokBatchSyncJobId = 1, VideoId = "V-SUCCEEDED", Status = TikTokBatchSyncJobItemStatus.Succeeded
        });
        await ctx.Db.SaveChangesAsync();

        result = await ctx.Service.PreviewControlledSingleItemAsync();
        Assert.False(result.IsAvailable);
        Assert.Contains("Pending", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ctx.Handler.RequestLog);
    }

    [Fact]
    public async Task Controlled_Execution_Uses_Job_Period_And_Only_Selected_Item()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var job = new TikTokBatchSyncJob
        {
            Id = 1,
            TikTokShopId = shop.Id,
            StartDate = new DateTime(2026, 10, 13),
            EndDate = new DateTime(2026, 10, 15),
            Status = TikTokBatchSyncJobStatus.ProcessingDetails,
            CollectionCompleted = true
        };
        ctx.Db.TikTokBatchSyncJobs.Add(job);
        await ctx.Db.SaveChangesAsync();
        var selected = new TikTokBatchSyncJobItem { TikTokBatchSyncJobId = 1, VideoId = "V-SELECTED", Status = TikTokBatchSyncJobItemStatus.Pending };
        var other = new TikTokBatchSyncJobItem { TikTokBatchSyncJobId = 1, VideoId = "V-OTHER", Status = TikTokBatchSyncJobItemStatus.Pending };
        ctx.Db.TikTokBatchSyncJobItems.AddRange(selected, other);
        ctx.Db.ContentLogs.Add(new ContentLog { TikTokShopId = shop.Id, VideoId = selected.VideoId });
        await ctx.Db.SaveChangesAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson(views: 42));

        var result = await ctx.Service.ExecuteControlledSingleItemAsync(1, selected.Id);

        Assert.True(result.Accepted);
        Assert.Equal("2026-10-13", ctx.Handler.DetailsRequestQueries.Single()["start_date_ge"]);
        Assert.Equal("2026-10-16", ctx.Handler.DetailsRequestQueries.Single()["end_date_lt"]);
        Assert.Equal(TikTokBatchSyncJobItemStatus.Succeeded, (await ctx.Db.TikTokBatchSyncJobItems.FindAsync(selected.Id))!.Status);
        Assert.Equal(TikTokBatchSyncJobItemStatus.Pending, (await ctx.Db.TikTokBatchSyncJobItems.FindAsync(other.Id))!.Status);
        Assert.Single(await ctx.Db.ContentLogs.Where(l => l.TikTokShopId == shop.Id && l.VideoId == selected.VideoId).ToListAsync());
        Assert.Single(await ctx.Db.ContentMetrics.ToListAsync());
    }

    [Fact]
    public async Task Controlled_Execution_Rejects_Item_That_Changed_After_Preview()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var job = await SeedControlledJobAsync(ctx, shop.Id);
        var item = await ctx.Db.TikTokBatchSyncJobItems.SingleAsync();
        item.Status = TikTokBatchSyncJobItemStatus.Succeeded;
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Service.ExecuteControlledSingleItemAsync(job.Id, item.Id);

        Assert.False(result.Accepted);
        Assert.Empty(ctx.Handler.RequestLog);
    }

    private static async Task<TikTokBatchSyncJob> SeedControlledJobAsync(BatchSyncTestContext ctx, long shopId)
    {
        var job = new TikTokBatchSyncJob
        {
            Id = 1, TikTokShopId = shopId,
            StartDate = new DateTime(2026, 10, 13), EndDate = new DateTime(2026, 10, 15),
            Status = TikTokBatchSyncJobStatus.ProcessingDetails, CollectionCompleted = true
        };
        ctx.Db.TikTokBatchSyncJobs.Add(job);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.TikTokBatchSyncJobItems.Add(new TikTokBatchSyncJobItem
        {
            TikTokBatchSyncJobId = 1, VideoId = "V-CHANGED", Status = TikTokBatchSyncJobItemStatus.Pending
        });
        await ctx.Db.SaveChangesAsync();
        return job;
    }

    [Fact]
    public async Task Batch_Details_Uses_Job_Period_Not_ContentLog_Post_Time()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var jobId = await ctx.Service.StartJobAsync(
            shop.Id,
            new DateTime(2026, 10, 13),
            new DateTime(2026, 10, 15));

        ctx.Handler.Responder = request =>
            request.RequestUri!.AbsolutePath.Contains("/shop_videos/performance", StringComparison.Ordinal)
                ? FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson(null, "V-PERIOD"))
                : FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson(views: 42));

        await ctx.Service.ProcessNextJobAsync();

        var query = Assert.Single(ctx.Handler.DetailsRequestQueries);
        Assert.Equal("2026-10-13", query["start_date_ge"]);
        Assert.Equal("2026-10-16", query["end_date_lt"]);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed,
            (await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId)).Status);
    }

    [Fact]
    public async Task Invalid_Explicit_Details_Period_Is_Rejected_Before_Http()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var log = new ContentLog { TikTokShopId = shop.Id, VideoId = "V-INVALID" };
        ctx.Db.ContentLogs.Add(log);
        await ctx.Db.SaveChangesAsync();

        var outcome = await ctx.DetailsService.SyncSingleContentLogDetailedAsync(
            log.Id,
            startDateInclusive: new DateTime(2026, 10, 16),
            endDateExclusive: new DateTime(2026, 10, 16));

        Assert.False(outcome.Synced);
        Assert.Empty(ctx.Handler.RequestLog);
    }

    // ---- details persistence + historical patching -----------------------------------

    [Fact]
    public async Task Details_Persist_ContentMetric_Via_Existing_Pipeline()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var job = await ctx.SeedCollectedJobAsync(shop.Id, "7687996887235890452");
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson(views: 263, likes: 13));

        await ctx.Service.ProcessNextJobAsync();

        var log = await ctx.Db.ContentLogs.SingleAsync(cl => cl.VideoId == "7687996887235890452");
        var metric = await ctx.Db.ContentMetrics.SingleAsync(m => m.ContentLogId == log.Id);

        // Verified field mapping from the real details response shape.
        Assert.Equal(263, metric.Views);
        Assert.Equal(13, metric.Likes);
        Assert.Equal(2, metric.Comments);
        Assert.Equal(3, metric.Shares);
        Assert.Equal(1, metric.NewFollowers);
        // Fields not present in the payload stay null (never fabricated).
        Assert.Null(metric.Reach);
        Assert.Null(metric.AverageWatch);
        Assert.Null(metric.FullWatchRate);

        var reloaded = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, reloaded.Status);
        Assert.Equal(1, reloaded.SucceededItemsCount);
    }

    [Fact]
    public async Task Full_Flow_Updates_Historical_ContentLog_Without_Duplicate()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        // Historical log that already exists for this (shop, video_id).
        var historical = new ContentLog
        {
            VideoId = "V-HIST",
            TikTokShopId = shop.Id,
            Title = "OLD TITLE",
            VideoPostTime = new DateTime(2026, 8, 1),
            ContentTypeId = ContentTypeClassifier.NON_KK_TYPE_ID
        };
        ctx.Db.ContentLogs.Add(historical);
        await ctx.Db.SaveChangesAsync();

        ctx.Handler.Responder = request =>
            request.RequestUri!.AbsolutePath.Contains("/shop_videos/performance")
                ? FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson(null, "V-HIST"))
                : FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson(views: 555));

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        // Exactly ONE ContentLog for (shop, video_id) - updated, never duplicated.
        var logs = await ctx.Db.ContentLogs.Where(cl => cl.VideoId == "V-HIST").ToListAsync();
        var log = Assert.Single(logs);
        Assert.Equal(shop.Id, log.TikTokShopId);
        Assert.Equal("Title V-HIST", log.Title);            // refreshed from list API
        Assert.Equal(new DateTime(2026, 9, 5, 10, 0, 0), log.VideoPostTime);
        Assert.Equal(historical.Id, log.Id);                 // same row, not a new one

        // List metric + details metric both attached to the SAME ContentLog.
        var metrics = await ctx.Db.ContentMetrics.Where(m => m.ContentLogId == log.Id).ToListAsync();
        Assert.Equal(2, metrics.Count);
        Assert.Contains(metrics, m => m.Views == 100);   // list-phase snapshot
        Assert.Contains(metrics, m => m.Views == 555);   // details snapshot

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, job.Status);
        Assert.Equal(1, job.SucceededItemsCount);
    }

    [Fact]
    public async Task Demographics_Are_Persisted_Only_When_Viewers_Profile_Present()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        await ctx.SeedCollectedJobAsync(shop.Id, "VD");
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(
            BatchSyncTestContext.DetailsJson(withViewersProfile: true));

        await ctx.Service.ProcessNextJobAsync();

        var metric = await ctx.Db.ContentMetrics.SingleAsync();
        Assert.NotNull(metric.DemographicsJson);
        Assert.Contains("male", metric.DemographicsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("18-24", metric.DemographicsJson);
        // No NEW_FOLLOWER profile in the payload -> that structure must stay null.
        Assert.Null(metric.NewFollowerDemographicsJson);
    }

    [Fact]
    public async Task Demographics_Stay_Null_When_Profile_Array_Is_Empty()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        await ctx.SeedCollectedJobAsync(shop.Id, "VE");
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(
            BatchSyncTestContext.DetailsJson(withViewersProfile: false));

        await ctx.Service.ProcessNextJobAsync();

        var metric = await ctx.Db.ContentMetrics.SingleAsync();
        Assert.Equal(263, metric.Views);          // engagement still saved
        Assert.Null(metric.DemographicsJson);      // empty viewer_profile -> null, not fabricated
        Assert.Null(metric.NewFollowerDemographicsJson);
    }

    // ---- content type classification ---------------------------------------------------

    [Fact]
    public async Task Classification_AutoGmvLive_Persisted_After_Details_Saved()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        await ctx.SeedCollectedJobAsync(shop.Id, "V-AL");

        var log = new ContentLog
        {
            VideoId = "V-AL",
            TikTokShopId = shop.Id,
            IsLive = true,
            HasCommerce = true,
            ContentTypeId = null
        };
        ctx.Db.ContentLogs.Add(log);
        await ctx.Db.SaveChangesAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        await ctx.Service.ProcessNextJobAsync();

        var reloaded = await ctx.Db.ContentLogs.SingleAsync(cl => cl.Id == log.Id);
        Assert.Equal(ContentTypeClassifier.AUTO_GMV_LIVE_TYPE_ID, reloaded.ContentTypeId);
    }

    [Fact]
    public async Task Classification_Live_Without_Commerce_Is_Not_AutoGmvLive()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        await ctx.SeedCollectedJobAsync(shop.Id, "V-LIVEONLY");

        var log = new ContentLog
        {
            VideoId = "V-LIVEONLY",
            TikTokShopId = shop.Id,
            IsLive = true,
            HasCommerce = false,
            ContentTypeId = null
        };
        ctx.Db.ContentLogs.Add(log);
        await ctx.Db.SaveChangesAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        await ctx.Service.ProcessNextJobAsync();

        var reloaded = await ctx.Db.ContentLogs.SingleAsync(cl => cl.Id == log.Id);
        Assert.Equal(ContentTypeClassifier.NON_KK_TYPE_ID, reloaded.ContentTypeId);
        Assert.NotEqual(ContentTypeClassifier.AUTO_GMV_LIVE_TYPE_ID, reloaded.ContentTypeId);
    }

    [Fact]
    public async Task Classification_KK_From_ProductsJson_Signal_And_Not_Downgraded_By_Details()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        await ctx.SeedCollectedJobAsync(shop.Id, "V-KK");

        var log = new ContentLog
        {
            VideoId = "V-KK",
            TikTokShopId = shop.Id,
            IsLive = null,
            HasCommerce = null,
            ContentTypeId = null
        };
        ctx.Db.ContentLogs.Add(log);
        await ctx.Db.SaveChangesAsync();

        // Historical list-phase metric carries the product signal.
        ctx.Db.ContentMetrics.Add(new ContentMetric
        {
            ContentLogId = log.Id,
            Views = 10,
            ProductsJson = "[{\"id\":\"p1\",\"name\":\"Produk\"}]",
            CapturedAt = DateTime.UtcNow.AddHours(-1)
        });
        await ctx.Db.SaveChangesAsync();

        // Details response has engagement but NO products.
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        await ctx.Service.ProcessNextJobAsync();

        var reloaded = await ctx.Db.ContentLogs.SingleAsync(cl => cl.Id == log.Id);
        // ProductsJson signal -> KK, even though the NEWEST metric snapshot has no products.
        Assert.Equal(ContentTypeClassifier.KK_TYPE_ID, reloaded.ContentTypeId);
    }

    [Fact]
    public async Task No_Fictional_Classification_When_No_Signal_Available()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        await ctx.SeedCollectedJobAsync(shop.Id, "V-NOSIG");

        var log = new ContentLog
        {
            VideoId = "V-NOSIG",
            TikTokShopId = shop.Id,
            IsLive = null,
            HasCommerce = null,
            ContentTypeId = null
        };
        ctx.Db.ContentLogs.Add(log);
        await ctx.Db.SaveChangesAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        var jobId = (await ctx.Db.TikTokBatchSyncJobs.SingleAsync()).Id;
        await ctx.Service.ProcessNextJobAsync();

        var reloaded = await ctx.Db.ContentLogs.SingleAsync(cl => cl.Id == log.Id);
        Assert.Null(reloaded.ContentTypeId); // honest state: no signal, no fabricated type

        var status = await ctx.Service.GetStatusAsync(jobId);
        Assert.Equal(1, status!.UnclassifiedVideos);
    }

    [Fact]
    public async Task Existing_Classification_Is_Not_Downgraded_Without_Signals()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        await ctx.SeedCollectedJobAsync(shop.Id, "V-KKOLD");

        var log = new ContentLog
        {
            VideoId = "V-KKOLD",
            TikTokShopId = shop.Id,
            IsLive = null,
            HasCommerce = null,
            ContentTypeId = ContentTypeClassifier.KK_TYPE_ID // historical classification
        };
        ctx.Db.ContentLogs.Add(log);
        await ctx.Db.SaveChangesAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        await ctx.Service.ProcessNextJobAsync();

        var reloaded = await ctx.Db.ContentLogs.SingleAsync(cl => cl.Id == log.Id);
        // Details snapshot without products must NOT downgrade KK -> NON_KK.
        Assert.Equal(ContentTypeClassifier.KK_TYPE_ID, reloaded.ContentTypeId);
    }

    [Fact]
    public void TryClassify_Returns_Null_When_No_Signal()
    {
        Assert.Null(ContentTypeClassifier.TryClassify(null, null, null));
        Assert.Null(ContentTypeClassifier.TryClassify(null, null, "[]"));
        Assert.Equal(ContentTypeClassifier.AUTO_GMV_LIVE_TYPE_ID, ContentTypeClassifier.TryClassify(true, true, null));
        Assert.Equal(ContentTypeClassifier.KK_TYPE_ID, ContentTypeClassifier.TryClassify(null, null, "[{\"id\":\"p\"}]"));
        // Existing priority and rules are untouched.
        Assert.Equal(ContentTypeClassifier.NON_KK_TYPE_ID, ContentTypeClassifier.ClassifyContentType(null, null, null));
    }

    // ---- rate limit / retry / failure isolation -----------------------------------------

    [Fact]
    public async Task Collection_Rate_Limit_Fails_Job_With_Bounded_Retries()
    {
        using var ctx = new BatchSyncTestContext(); // MaxAttempts = 3
        var shop = await ctx.AddShopAsync(cipher: "SHOP-RL-COLLECT");
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json("{\"error\":\"too many\"}", HttpStatusCode.TooManyRequests);

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        // A 429 is not retried in a tight in-process loop: the job is parked with NextAttemptAt.
        // Drive the bounded retries across worker passes (each pass makes exactly one attempt).
        for (var i = 0; i < 3; i++)
        {
            var current = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
            if (current.Status == TikTokBatchSyncJobStatus.Failed) break;

            // Bring the parked job due while keeping it parked (NextAttemptAt non-null).
            current.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            await ctx.Db.SaveChangesAsync();
            await ctx.Service.ProcessNextJobAsync();
        }

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.Failed, job.Status);
        Assert.Contains("collection failed after 3 attempt", job.LastError);
        Assert.Equal(3, ctx.Handler.RequestLog.Count); // bounded, no infinite loop
        Assert.DoesNotContain(ctx.Handler.RequestLog, x => x.StartsWith("details:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rate_Limited_Details_Parks_The_Job_Without_Failing_Untried_Items()
    {
        using var ctx = new BatchSyncTestContext(); // MaxRateLimitPauses = 2
        var shop = await ctx.AddShopAsync(cipher: "SHOP-RL-DETAILS"); // unique: shop backoff state is static
        await ctx.SeedCollectedJobAsync(shop.Id, "V-RL", "V-RL2");
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(
            BatchSyncTestContext.DetailsJson(), HttpStatusCode.TooManyRequests, retryAfter: "0.1");

        await ctx.Service.ProcessNextJobAsync();

        // A throttle parks the whole job (persistent NextAttemptAt) instead of mass-failing it.
        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync();
        Assert.Equal(TikTokBatchSyncJobStatus.ProcessingDetails, job.Status);
        Assert.NotNull(job.NextAttemptAt);
        Assert.True(job.NextAttemptAt > DateTime.UtcNow);
        Assert.Equal(0, job.FailedItemsCount);

        // Items that were never tried must stay Pending - never Failed, never NotAttempted.
        var items = await ctx.Db.TikTokBatchSyncJobItems.ToListAsync();
        Assert.All(items, i => Assert.Equal(TikTokBatchSyncJobItemStatus.Pending, i.Status));
        Assert.All(items, i => Assert.Null(i.ProcessedAt));

        var status = await ctx.Service.GetStatusAsync(job.Id);
        Assert.NotNull(status);
        Assert.True(status!.IsActive);
        Assert.Equal(0, status.Failed);
        Assert.Equal(0, status.NotAttempted);
        Assert.Equal(2, status.Pending);

        // Bounded: exactly one pause cycle x 5 internal retry attempts = 5 detail HTTP calls.
        var detailCalls = ctx.Handler.RequestLog.Count(x => x.StartsWith("details:", StringComparison.Ordinal));
        Assert.Equal(5, detailCalls);
    }

    [Fact]
    public async Task Permanent_Detail_Failure_Does_Not_Stop_Other_Items()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        await ctx.SeedCollectedJobAsync(shop.Id, "V-BAD", "V-GOOD");

        ctx.Handler.Responder = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/shop_videos/performance")) return FakeTikTokApiHandler.Json(BatchSyncTestContext.EmptyListJson());
            if (FakeTikTokApiHandler.TargetsVideo(request, "V-BAD")) return FakeTikTokApiHandler.Json("{\"code\":40401,\"message\":\"video not found\"}", HttpStatusCode.NotFound);
            return FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());
        };

        await ctx.Service.ProcessNextJobAsync();

        var items = await ctx.Db.TikTokBatchSyncJobItems.OrderBy(i => i.VideoId).ToListAsync();
        var bad = items.Single(i => i.VideoId == "V-BAD");
        var good = items.Single(i => i.VideoId == "V-GOOD");
        Assert.Equal(TikTokBatchSyncJobItemStatus.Failed, bad.Status);
        Assert.Equal(TikTokBatchSyncJobItemStatus.Succeeded, good.Status);
        Assert.False(string.IsNullOrWhiteSpace(bad.LastError));

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync();
        Assert.Equal(TikTokBatchSyncJobStatus.CompletedWithErrors, job.Status);
        Assert.Equal(1, job.SucceededItemsCount);
        Assert.Equal(1, job.FailedItemsCount);

        // Both videos were attempted; the failure did not abort the job.
        Assert.Contains("details:V-BAD", ctx.Handler.RequestLog);
        Assert.Contains("details:V-GOOD", ctx.Handler.RequestLog);
    }

    [Fact]
    public async Task Transient_Detail_Failure_Retries_Up_To_Item_Limit_Then_Fails_That_Item()
    {
        using var ctx = new BatchSyncTestContext(); // MaxItemAttempts = 3
        var shop = await ctx.AddShopAsync();
        await ctx.SeedCollectedJobAsync(shop.Id, "V-T1", "V-T2");

        ctx.Handler.Responder = request =>
        {
            if (FakeTikTokApiHandler.TargetsVideo(request, "V-T1"))
                return FakeTikTokApiHandler.Json("{\"error\":\"server\"}", HttpStatusCode.InternalServerError);
            return FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());
        };

        await ctx.Service.ProcessNextJobAsync();

        var t1 = await ctx.Db.TikTokBatchSyncJobItems.SingleAsync(i => i.VideoId == "V-T1");
        var t2 = await ctx.Db.TikTokBatchSyncJobItems.SingleAsync(i => i.VideoId == "V-T2");

        Assert.Equal(TikTokBatchSyncJobItemStatus.Failed, t1.Status);
        Assert.Equal(3, t1.AttemptCount); // bounded retries for the failing item
        Assert.Equal(TikTokBatchSyncJobItemStatus.Succeeded, t2.Status); // other items still processed

        var attemptsOnT1 = ctx.Handler.RequestLog.Count(x => x == "details:V-T1");
        Assert.Equal(3, attemptsOnT1);

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync();
        Assert.Equal(TikTokBatchSyncJobStatus.CompletedWithErrors, job.Status);
        Assert.Contains("HTTP 500", t1.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 of 2 video(s) failed", job.LastError, StringComparison.Ordinal);
        // The summary names the dominant cause instead of only the first error encountered.
        Assert.Contains("Dominant cause:", job.LastError, StringComparison.Ordinal);
    }
}
