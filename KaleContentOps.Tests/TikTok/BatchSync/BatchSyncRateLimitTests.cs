using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KaleContentOps.Tests.TikTok.BatchSync;

/// <summary>
/// Rate-limit handling for the persistent batch sync job.
///
/// The previous behaviour marked every remaining item Failed ("Rate limit retry budget exhausted")
/// the moment the pause budget ran out - so one HTTP 429 could turn thousands of never-attempted
/// videos into fake item failures. These tests pin the corrected contract:
///   - a throttle parks the JOB (persistent NextAttemptAt) and releases it, it does not fail items;
///   - items that were never tried keep Pending, and are recorded as NotAttempted (not Failed)
///     only when the bounded budget finally gives up;
///   - the cooldown honors a valid Retry-After header, bounded by configuration;
///   - the job error summary reports the dominant cause.
/// </summary>
public class BatchSyncRateLimitTests
{
    [Fact]
    public async Task Parked_Rate_Limited_Job_Resumes_And_Completes_When_Throttle_Clears()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync(cipher: "SHOP-RL-RESUME"); // unique: shop backoff state is static
        var job = await ctx.SeedCollectedJobAsync(shop.Id, "V-R1", "V-R2");

        var throttled = true;
        ctx.Handler.Responder = _ => throttled
            ? FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson(), HttpStatusCode.TooManyRequests, retryAfter: "0.1")
            : FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        await ctx.Service.ProcessNextJobAsync();

        var parked = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(TikTokBatchSyncJobStatus.ProcessingDetails, parked.Status);
        Assert.NotNull(parked.NextAttemptAt);
        Assert.Equal(1, parked.AttemptCount); // consecutive-pause counter
        Assert.Equal(0, parked.FailedItemsCount);

        // A worker pass BEFORE NextAttemptAt must not touch the parked job.
        parked.NextAttemptAt = DateTime.UtcNow.AddMinutes(5);
        await ctx.Db.SaveChangesAsync();
        Assert.Null(await ctx.Service.ProcessNextJobAsync());

        // Clear the throttle and let the SAME job resume. The shop-level suspension guard is
        // process-wide and deliberately brief here, so give it a moment to lapse.
        throttled = false;
        await Task.Delay(250);

        parked = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == job.Id);
        parked.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        await ctx.Db.SaveChangesAsync();
        await ctx.Service.ProcessNextJobAsync();

        var finished = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, finished.Status);
        Assert.Equal(2, finished.SucceededItemsCount);
        Assert.Equal(0, finished.FailedItemsCount);
        Assert.Equal(0, finished.AttemptCount); // reset after real progress
        Assert.Null(finished.NextAttemptAt);

        // Resume means the same job, not a new one.
        Assert.Equal(1, await ctx.Db.TikTokBatchSyncJobs.CountAsync());
    }

    [Fact]
    public async Task Rate_Limit_Budget_Exhaustion_Marks_Untried_Items_NotAttempted_Not_Failed()
    {
        var options = new TikTokOptions
        {
            AppKey = "K",
            AppSecret = "S",
            BatchSyncRetryBaseSeconds = 0,
            BatchSyncMaxAttempts = 3,
            BatchSyncMaxItemAttempts = 3,
            BatchSyncMaxRateLimitPauses = 1,
            BatchSyncMaxRunMinutes = 1,
            BatchSyncLeaseMinutes = 10,
            BatchSyncMaxPages = 50,
            BatchSyncMaxRangeDays = 366
        };
        using var ctx = new BatchSyncTestContext(options);
        var shop = await ctx.AddShopAsync(cipher: "SHOP-RL-ABANDON");
        var job = await ctx.SeedCollectedJobAsync(shop.Id, "V-A1", "V-A2", "V-A3");

        // The job's first claim was hours ago: its bounded rate-limit budget is spent.
        job.StartedAt = DateTime.UtcNow.AddHours(-2);
        await ctx.Db.SaveChangesAsync();

        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(
            BatchSyncTestContext.DetailsJson(), HttpStatusCode.TooManyRequests, retryAfter: "0.1");

        await ctx.Service.ProcessNextJobAsync();

        var finished = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(TikTokBatchSyncJobStatus.Failed, finished.Status); // nothing succeeded
        Assert.Equal(0, finished.FailedItemsCount);                     // no fake failures
        Assert.Null(finished.NextAttemptAt);                            // terminal
        Assert.NotNull(finished.CompletedAt);

        var items = await ctx.Db.TikTokBatchSyncJobItems.OrderBy(i => i.VideoId).ToListAsync();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(TikTokBatchSyncJobItemStatus.NotAttempted, i.Status));
        Assert.All(items, i => Assert.Contains("Not attempted", i.LastError));
        Assert.All(items, i => Assert.Equal(0, i.AttemptCount)); // genuinely never tried
        Assert.All(items, i => Assert.Null(i.ProcessedAt));

        Assert.Contains("not attempted", finished.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dominant cause:", finished.LastError, StringComparison.Ordinal);
        // The old (dishonest) blanket reason must be gone.
        Assert.DoesNotContain("Rate limit retry budget exhausted", finished.LastError, StringComparison.OrdinalIgnoreCase);

        var status = await ctx.Service.GetStatusAsync(job.Id);
        Assert.NotNull(status);
        Assert.Equal(3, status!.NotAttempted);
        Assert.Equal(0, status.Failed);
        Assert.Equal(3, status.TotalVideoIdsFound);
        Assert.False(status.IsActive);
    }

    [Fact]
    public async Task Job_Error_Summary_Reports_The_Dominant_Cause_Not_The_First_Error()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync(cipher: "SHOP-DOMINANT");
        await ctx.SeedCollectedJobAsync(shop.Id, "V-D1", "V-D2", "V-D3");

        ctx.Handler.Responder = request =>
        {
            // One permanent 404 plus two identical 500s: the 500s are the dominant cause.
            // Video identity matches EITHER shape (path for legacy, video_ids for batch).
            return FakeTikTokApiHandler.TargetsVideo(request, "V-D3")
                ? FakeTikTokApiHandler.Json("{\"error\":\"gone\"}", HttpStatusCode.NotFound)
                : FakeTikTokApiHandler.Json("{\"error\":\"server\"}", HttpStatusCode.InternalServerError);
        };

        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync();
        Assert.Equal(TikTokBatchSyncJobStatus.Failed, job.Status);
        Assert.Equal(3, job.FailedItemsCount);
        Assert.Equal(0, await ctx.Db.TikTokBatchSyncJobItems
            .CountAsync(i => i.Status == TikTokBatchSyncJobItemStatus.NotAttempted));

        Assert.Contains("3 of 3 video(s) failed.", job.LastError);
        Assert.Contains("Dominant cause: 2 of 3", job.LastError, StringComparison.Ordinal);
        Assert.Contains("HTTP 500", job.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rate_Limit_Cooldown_Honors_Retry_After_And_Is_Bounded()
    {
        using var ctx = new BatchSyncTestContext();

        // Focused check of the cooldown policy itself: driving it through HTTP would need real
        // multi-second Retry-After waits, which the suite must not pay.
        var method = typeof(TikTokBatchSyncService).GetMethod(
            "ComputeRateLimitCooldown", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        TimeSpan Invoke(int attempt, int? retryAfter) =>
            (TimeSpan)method!.Invoke(ctx.Service, new object?[] { attempt, retryAfter })!;

        // A valid Retry-After longer than the local backoff is honored...
        Assert.Equal(TimeSpan.FromSeconds(42), Invoke(1, 42));
        // ...but never beyond the configured bound (default BatchSyncMaxCooldownSeconds = 900).
        Assert.Equal(TimeSpan.FromSeconds(900), Invoke(1, 99_999));
        // Missing / non-positive hints fall back to the bounded local backoff (1s floor in tests).
        Assert.Equal(TimeSpan.FromSeconds(1), Invoke(1, null));
        Assert.Equal(TimeSpan.FromSeconds(1), Invoke(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(1), Invoke(1, -5));
    }

    [Fact]
    public async Task Details_Retry_After_Header_Is_Read_From_The_Throttle_Response()
    {
        using var ctx = new BatchSyncTestContext();
        await ctx.AddShopAsync(cipher: "SHOP-RETRY-AFTER");
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(
            BatchSyncTestContext.DetailsJson(), HttpStatusCode.TooManyRequests, retryAfter: "0.2");

        var res = await ((TikTokVideoService)ctx.VideoService)
            .RunDetailsDiagnosticAsync("V-RA", "SHOP-RETRY-AFTER");

        Assert.Equal(429, res.StatusCode);
        Assert.Equal(1, res.RetryAfterSeconds); // ceil(0.2) -> 1s; the job bounds it further
    }
}
