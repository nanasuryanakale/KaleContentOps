using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KaleContentOps.Tests.TikTok.BatchSync;

public class BatchSyncJobLifecycleTests
{
    // ---- job creation & validation -------------------------------------------------

    [Fact]
    public async Task StartJob_Creates_Queued_Job_With_Inclusive_Dates()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.Queued, job.Status);
        Assert.Equal(new DateTime(2026, 9, 1), job.StartDate);
        Assert.Equal(new DateTime(2026, 9, 7), job.EndDate); // stored inclusive as selected
        Assert.False(job.CollectionCompleted);
        Assert.Null(job.CurrentPageToken);
        Assert.Equal(0, job.TotalVideoIdsFound);
    }

    [Fact]
    public async Task StartJob_Rejects_End_Before_Start()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 7), new DateTime(2026, 9, 1)));
    }

    [Fact]
    public async Task StartJob_Rejects_Range_Longer_Than_Limit()
    {
        using var ctx = new BatchSyncTestContext(); // BatchSyncMaxRangeDays = 366
        var shop = await ctx.AddShopAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ctx.Service.StartJobAsync(shop.Id, new DateTime(2025, 1, 1), new DateTime(2026, 9, 1)));
    }

    [Fact]
    public async Task StartJob_Rejects_Duplicate_Active_Job_For_Same_Shop()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7)));
        Assert.Contains("active batch sync job", ex.Message);
    }

    [Fact]
    public async Task StartJob_Rejects_Shop_Without_Cipher()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = new TikTokShop { ShopName = "No cipher" };
        ctx.Db.TikTokShops.Add(shop);
        await ctx.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7)));
        Assert.Contains("cipher", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- date-range semantics ------------------------------------------------------

    [Fact]
    public async Task List_Request_Uses_StartDateGe_And_EndDateLt_Exclusive()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.EmptyListJson());

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var query = Assert.Single(ctx.Handler.ListRequestQueries);
        Assert.Equal("2026-09-01", query["start_date_ge"]);
        // UI End Date is inclusive -> API end_date_lt must be endDate + 1 day.
        Assert.Equal("2026-09-08", query["end_date_lt"]);
        Assert.Equal("100", query["page_size"]);
        Assert.Equal(shop.ShopCipher, query["shop_cipher"]);

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.True(job.CollectionCompleted);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, job.Status); // zero videos -> completed
    }

    // ---- pagination + dedup ---------------------------------------------------------

    [Fact]
    public async Task MultiPage_Collection_Dedupes_Videos_And_Details_Run_Only_After_All_Pages()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        ctx.Handler.Responder = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/shop_videos/performance"))
            {
                var query = request.RequestUri.Query;
                if (!query.Contains("page_token"))
                    return FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson("t2", "V1", "V2"));
                if (query.Contains("t2"))
                    return FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson("t3", "V2", "V3"));
                return FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson(null, "V4"));
            }
            return FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        // Dedup: V2 appears on two pages but is queued exactly once.
        var itemVideoIds = await ctx.Db.TikTokBatchSyncJobItems
            .Where(i => i.TikTokBatchSyncJobId == jobId)
            .Select(i => i.VideoId)
            .ToListAsync();
        Assert.Equal(new[] { "V1", "V2", "V3", "V4" }, itemVideoIds.OrderBy(x => x).ToArray());

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.True(job.CollectionCompleted);
        Assert.Equal(3, job.TotalPagesCollected);
        Assert.Equal(4, job.TotalVideoIdsFound);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, job.Status);
        Assert.Equal(4, job.SucceededItemsCount);

        // ALL list requests happened before the FIRST details request.
        var log = ctx.Handler.RequestLog;
        var firstDetails = log.FindIndex(x => x.StartsWith("details:", StringComparison.Ordinal));
        var lastList = log.FindLastIndex(x => x.StartsWith("list:", StringComparison.Ordinal));
        Assert.True(firstDetails > 0, "details phase must run after collection");
        Assert.True(lastList < firstDetails, "details must never start before every list page was fetched");

        // One details call per queued video id.
        Assert.Equal(4, log.Count(x => x.StartsWith("details:", StringComparison.Ordinal)));
    }

    // ---- checkpoint / recovery ------------------------------------------------------

    [Fact]
    public async Task Collection_Resumes_From_Checkpoint_After_Crash()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        // Crashed worker: page 1 was already persisted (checkpoint = t2), lease expired.
        var job = await ctx.SeedStaleActiveJobAsync(
            shop.Id, TikTokBatchSyncJobStatus.CollectingIds,
            collectionCompleted: false, currentPageToken: "t2",
            ("V1", TikTokBatchSyncJobItemStatus.Pending, 0));

        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson(null, "V2"));
        // default details responder inside JSON path: list only; add details:
        ctx.Handler.Responder = request =>
            request.RequestUri!.AbsolutePath.Contains("/shop_videos/performance")
                ? FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson(null, "V2"))
                : FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        await ctx.Service.ProcessNextJobAsync();

        // The FIRST list call must resume from the persisted checkpoint, not restart.
        Assert.Equal("t2", ctx.Handler.ListRequestQueries[0]["page_token"]);

        var reloaded = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == job.Id);
        Assert.True(reloaded.CollectionCompleted);
        Assert.Null(reloaded.CurrentPageToken);
        Assert.Equal(2, reloaded.TotalVideoIdsFound);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, reloaded.Status);

        var videoIds = await ctx.Db.TikTokBatchSyncJobItems
            .Where(i => i.TikTokBatchSyncJobId == job.Id)
            .Select(i => i.VideoId).ToListAsync();
        Assert.Equal(new[] { "V1", "V2" }, videoIds.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Transient_Page_Failure_Is_Retried_Without_Losing_Checkpoint()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        var page2Calls = 0;
        ctx.Handler.Responder = request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/shop_videos/performance"))
            {
                if (!request.RequestUri.Query.Contains("page_token"))
                    return FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson("t2", "V1"));
                page2Calls++;
                if (page2Calls == 1)
                    return FakeTikTokApiHandler.Json("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError);
                return FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson(null, "V2"));
            }
            return FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.True(job.CollectionCompleted);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, job.Status);
        Assert.Equal(2, job.TotalVideoIdsFound);
        Assert.Equal(0, job.AttemptCount); // reset after phase success

        // The failed page was retried with the SAME checkpoint token.
        Assert.Equal(2, page2Calls);
        var tokens = ctx.Handler.ListRequestQueries.Select(q => q.TryGetValue("page_token", out var t) ? t : "").ToList();
        Assert.Equal(new[] { "", "t2", "t2" }, tokens);
    }

    [Fact]
    public async Task Collection_Retry_Exhaustion_Fails_Job_And_Never_Starts_Details()
    {
        using var ctx = new BatchSyncTestContext(); // BatchSyncMaxAttempts = 3
        var shop = await ctx.AddShopAsync();

        ctx.Handler.Responder = request =>
            request.RequestUri!.AbsolutePath.Contains("/shop_videos/performance")
                ? FakeTikTokApiHandler.Json("{\"error\":\"down\"}", HttpStatusCode.InternalServerError)
                : FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.Failed, job.Status);
        Assert.False(job.CollectionCompleted, "a partial/failed collection must never be treated as complete");
        Assert.Contains("collection failed after 3 attempt", job.LastError);

        // Bounded retries: exactly 3 list attempts, no details call at all.
        Assert.Equal(3, ctx.Handler.RequestLog.Count(x => x.StartsWith("list:", StringComparison.Ordinal)));
        Assert.DoesNotContain(ctx.Handler.RequestLog, x => x.StartsWith("details:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pagination_Token_Loop_Is_Guarded()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        ctx.Handler.Responder = _ =>
            FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson("SAME", "V1"));

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.Failed, job.Status);
        Assert.Contains("did not advance", job.LastError);
        Assert.Equal(2, ctx.Handler.ListRequestQueries.Count); // no infinite loop
    }

    // ---- claim / locking -------------------------------------------------------------

    [Fact]
    public async Task Job_Claim_Is_Single_Winner_Between_Two_Workers()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        var worker2 = ctx.CreateSecondWorker();

        Assert.True(await ctx.Service.TryClaimJobAsync(jobId));
        Assert.False(await worker2.TryClaimJobAsync(jobId), "second worker must not win an already-claimed job");

        // While the claim lease is fresh, no other worker may pick the job up.
        Assert.Null(await worker2.ProcessNextJobAsync());

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.CollectingIds, job.Status);
        Assert.NotNull(job.UpdatedAt);
    }

    [Fact]
    public async Task Stale_Active_Job_Is_Reclaimed_Once_After_Lease_Expiry()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var job = await ctx.SeedStaleActiveJobAsync(
            shop.Id, TikTokBatchSyncJobStatus.CollectingIds,
            collectionCompleted: false, currentPageToken: "t1");

        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.EmptyListJson());

        Assert.True(await ctx.Service.TryClaimJobAsync(job.Id));

        var worker2 = ctx.CreateSecondWorker();
        Assert.False(await worker2.TryClaimJobAsync(job.Id), "takeover must also be single-winner");
    }

    [Fact]
    public async Task Job_In_Backoff_Is_Not_Processed_Before_NextAttemptAt()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var job = await ctx.SeedStaleActiveJobAsync(
            shop.Id, TikTokBatchSyncJobStatus.Queued,
            collectionCompleted: false, currentPageToken: null);
        job.UpdatedAt = null;
        job.NextAttemptAt = DateTime.UtcNow.AddMinutes(5);
        await ctx.Db.SaveChangesAsync();

        Assert.Null(await ctx.Service.ProcessNextJobAsync());
        Assert.Empty(ctx.Handler.RequestLog);
    }

    // ---- counters / status DTO ---------------------------------------------------------

    [Fact]
    public async Task Status_Dto_And_Job_Counters_Mirror_Item_States()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        ctx.Handler.Responder = request =>
            request.RequestUri!.AbsolutePath.Contains("/shop_videos/performance")
                ? FakeTikTokApiHandler.Json(BatchSyncTestContext.ListPageJson(null, "V1", "V2", "V3"))
                : FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var status = await ctx.Service.GetStatusAsync(jobId);
        Assert.NotNull(status);
        Assert.False(status!.IsActive);
        Assert.True(status.CollectionCompleted);
        Assert.Equal(3, status.TotalVideoIdsFound);
        Assert.Equal(3, status.Succeeded);
        Assert.Equal(0, status.Pending);
        Assert.Equal(0, status.Processing);
        Assert.Equal(0, status.Failed);
        Assert.Equal("Completed", status.Phase);

        // Stored counters on the job row match the same item states.
        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(job.SucceededItemsCount, status.Succeeded);
        Assert.Equal(job.PendingItemsCount, status.Pending);
        Assert.Equal(job.TotalVideoIdsFound, status.TotalVideoIdsFound);

        // And both match the actual item rows.
        var byStatus = await ctx.Db.TikTokBatchSyncJobItems
            .Where(i => i.TikTokBatchSyncJobId == jobId)
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();
        Assert.Equal(3, byStatus.Sum(x => x.Count));
        Assert.All(byStatus, x => Assert.Equal(TikTokBatchSyncJobItemStatus.Succeeded, x.Status));
    }

    [Fact]
    public async Task Restart_Recovery_Releases_Stuck_Processing_Item_And_Skips_Succeeded()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        // Crashed worker mid-details: A succeeded, B stuck in Processing, C untouched.
        var job = await ctx.SeedStaleActiveJobAsync(
            shop.Id, TikTokBatchSyncJobStatus.ProcessingDetails,
            collectionCompleted: true, currentPageToken: null,
            ("VA", TikTokBatchSyncJobItemStatus.Succeeded, 1),
            ("VB", TikTokBatchSyncJobItemStatus.Processing, 1),
            ("VC", TikTokBatchSyncJobItemStatus.Pending, 0));

        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        await ctx.Service.ProcessNextJobAsync();

        var items = await ctx.Db.TikTokBatchSyncJobItems
            .Where(i => i.TikTokBatchSyncJobId == job.Id)
            .OrderBy(i => i.VideoId)
            .ToListAsync();
        Assert.All(items, i => Assert.Equal(TikTokBatchSyncJobItemStatus.Succeeded, i.Status));

        // Successful item is NOT re-processed after restart.
        Assert.DoesNotContain("details:VA", ctx.Handler.RequestLog);
        // Stuck Processing item was released and processed.
        Assert.Contains("details:VB", ctx.Handler.RequestLog);
        Assert.Contains("details:VC", ctx.Handler.RequestLog);

        var reloaded = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, reloaded.Status);
        Assert.Equal(3, reloaded.SucceededItemsCount);
    }

    [Fact]
    public async Task Status_Concurrency_Token_Enforces_Compares_And_Swap_At_Model_Level()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        // Two workers load the SAME job (both see Status=Queued), then both try to claim.
        using var dbA = ctx.CreateSecondContext();
        using var dbB = ctx.CreateSecondContext();
        var jobA = await dbA.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        var jobB = await dbB.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);

        jobA.Status = TikTokBatchSyncJobStatus.CollectingIds;
        jobA.UpdatedAt = DateTime.UtcNow;
        await dbA.SaveChangesAsync(); // first writer wins

        jobB.Status = TikTokBatchSyncJobStatus.CollectingIds;
        jobB.UpdatedAt = DateTime.UtcNow;
        // Second writer must lose the CAS on the Status concurrency token.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task Collection_DNS_Network_Failure_Sets_Retry_And_Labels_Error_Safely()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        // Simulate a DNS/connection failure that is persistent (always fails)
        ctx.Handler.Responder = _ =>
        {
            var ex = new HttpRequestException("No such host is known. (open-api.tiktokglobalshop.com:443)", null);
            throw ex;
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        // First ProcessNextJobAsync call: collection fails with DNS error, schedules retry
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        // After transient failure, job should be in retry state (not Failed)
        Assert.Equal(TikTokBatchSyncJobStatus.CollectingIds, job.Status);
        Assert.False(job.CollectionCompleted, "failed collection must not be marked complete");
        Assert.NotNull(job.NextAttemptAt);
        Assert.True(job.NextAttemptAt > DateTime.UtcNow, "NextAttemptAt must be a future time");

        // Error text should safely label it as DNS/network
        Assert.Contains("DNS or network connection failure", job.LastError);
        Assert.Equal(1, job.AttemptCount);
    }

    [Fact]
    public async Task Collection_Timeout_Network_Failure_Sets_Retry_And_Labels_Error_Safely()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        // Simulate a timeout/connection failure (e.g., firewall, TCP RST)
        ctx.Handler.Responder = _ =>
        {
            var timeoutEx = new TaskCanceledException("The operation timed out.");
            throw timeoutEx;
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.CollectingIds, job.Status); // transient: not failed
        Assert.False(job.CollectionCompleted, "failed collection must not be marked complete");
        Assert.NotNull(job.NextAttemptAt);
        Assert.Equal(1, job.AttemptCount);
    }

    [Fact]
    public async Task History_Returns_Jobs_Newest_First()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.EmptyListJson());

        var first = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 3));
        await ctx.Service.ProcessNextJobAsync();
        var second = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 4), new DateTime(2026, 9, 6));

        var history = await ctx.Service.GetHistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(second, history[0].JobId);
        Assert.Equal(first, history[1].JobId);
    }
}
