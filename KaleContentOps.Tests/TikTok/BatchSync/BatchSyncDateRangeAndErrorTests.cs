using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KaleContentOps.Tests.TikTok.BatchSync;

/// <summary>
/// Tests for date range validation, inclusive/exclusive end date semantics, and error classification.
/// </summary>
public class BatchSyncDateRangeAndErrorTests
{
    // ---- Date Range Validation Tests ------------------------------------------------

    [Fact]
    public async Task StartJob_Accepts_Same_Start_And_End_Date()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 5), new DateTime(2026, 9, 5));

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.Queued, job.Status);
        Assert.Equal(new DateTime(2026, 9, 5), job.StartDate);
        Assert.Equal(new DateTime(2026, 9, 5), job.EndDate);
    }

    [Fact]
    public async Task StartJob_Accepts_Exactly_MaxRangeDays()
    {
        // Default BatchSyncMaxRangeDays = 366 (full year + 1)
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        var start = new DateTime(2026, 1, 1);
        var end = new DateTime(2026, 12, 31); // 365 days inclusive
        var daysDiff = (end - start).Days + 1;
        Assert.Equal(365, daysDiff);

        var jobId = await ctx.Service.StartJobAsync(shop.Id, start, end);

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.Queued, job.Status);
    }

    [Theory]
    [InlineData(0, 1)] // 0 days
    [InlineData(-10, -5)] // negative
    [InlineData(1, 1)] // single day (should be OK)
    public async Task StartJob_Date_Range_Boundary_Cases(int startOffsetDays, int endOffsetDays)
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var baseDate = new DateTime(2026, 9, 15);

        if (startOffsetDays > endOffsetDays && startOffsetDays != 0)
        {
            // End before start - should fail
            await Assert.ThrowsAsync<ArgumentException>(() =>
                ctx.Service.StartJobAsync(shop.Id, baseDate.AddDays(startOffsetDays), baseDate.AddDays(endOffsetDays)));
        }
        else
        {
            // Valid range
            var jobId = await ctx.Service.StartJobAsync(shop.Id,
                baseDate.AddDays(startOffsetDays),
                baseDate.AddDays(endOffsetDays));
            var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal(TikTokBatchSyncJobStatus.Queued, job.Status);
        }
    }

    [Fact]
    public async Task List_Request_Converts_Inclusive_End_To_Exclusive_EndDateLt()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.EmptyListJson());

        // UI user selects September 1-15 (inclusive)
        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 15));
        await ctx.Service.ProcessNextJobAsync();

        var query = Assert.Single(ctx.Handler.ListRequestQueries);
        // API request: start_date_ge = 2026-09-01, end_date_lt = 2026-09-16 (one day after end)
        Assert.Equal("2026-09-01", query["start_date_ge"]);
        Assert.Equal("2026-09-16", query["end_date_lt"]);
    }

    [Fact]
    public async Task List_Request_For_Single_Day_Range()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.EmptyListJson());

        // Single day selection
        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 15), new DateTime(2026, 9, 15));
        await ctx.Service.ProcessNextJobAsync();

        var query = Assert.Single(ctx.Handler.ListRequestQueries);
        Assert.Equal("2026-09-15", query["start_date_ge"]);
        Assert.Equal("2026-09-16", query["end_date_lt"]);
    }

    // ---- per-video details window (start_date_ge / end_date_lt) ----------------------

    [Theory]
    [InlineData("2026-09-05T10:00:00", "2026-09-05", "2026-09-06")] // ordinary post day
    [InlineData("2026-09-05T23:30:00", "2026-09-05", "2026-09-06")] // late Jakarta evening keeps its own date
    [InlineData("2026-09-30T01:00:00", "2026-09-30", "2026-10-01")] // month boundary
    [InlineData("2026-12-31T22:00:00", "2026-12-31", "2027-01-01")] // year boundary
    public async Task Details_Window_Is_The_Video_Post_Day_With_Exclusive_End(
        string postTime, string expectedStartGe, string expectedEndLt)
    {
        // Pins the payload actually sent: the details call asks for exactly the video's post
        // calendar day, with end_date_lt one day later (TikTok's end_date_lt is exclusive, so
        // start_date_ge=end_date_lt-1 day = a one-day window). Nothing is inferred from the UTC
        // clock here - the date comes from the post time TikTok itself returned.
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        ctx.Db.ContentLogs.Add(new ContentLog
        {
            VideoId = "V-DT",
            TikTokShopId = shop.Id,
            ProductionMethodId = 1,
            VideoPostTime = DateTime.Parse(postTime, System.Globalization.CultureInfo.InvariantCulture)
        });
        await ctx.Db.SaveChangesAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        await ((TikTokVideoService)ctx.VideoService).RunDetailsDiagnosticAsync("V-DT", "SHOP-TEST");

        var query = Assert.Single(ctx.Handler.DetailsRequestQueries);
        Assert.Equal(expectedStartGe, query["start_date_ge"]);
        Assert.Equal(expectedEndLt, query["end_date_lt"]);
    }

    [Fact]
    public async Task Details_Window_Falls_Back_To_Shop_Local_Last_30_Days_When_Post_Time_Is_Unknown()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        ctx.Db.ContentLogs.Add(new ContentLog
        {
            VideoId = "V-NODATE",
            TikTokShopId = shop.Id,
            ProductionMethodId = 1,
            VideoPostTime = null
        });
        await ctx.Db.SaveChangesAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());

        await ((TikTokVideoService)ctx.VideoService).RunDetailsDiagnosticAsync("V-NODATE", "SHOP-TEST");

        // The fallback is anchored on the shop reporting calendar day (Asia/Jakarta), never on
        // DateTime.UtcNow.Date. Same source of truth as the date picker.
        var shopTimeZone = new KaleContentOps.Services.ShopTimeZone(
            Microsoft.Extensions.Options.Options.Create(new KaleContentOps.Services.ShopTimeZoneOptions()));
        var today = shopTimeZone.TodayMidnight();

        var query = Assert.Single(ctx.Handler.DetailsRequestQueries);
        Assert.Equal(today.AddDays(-30).ToString("yyyy-MM-dd"), query["start_date_ge"]);
        Assert.Equal(today.AddDays(1).ToString("yyyy-MM-dd"), query["end_date_lt"]);
    }

    // ---- Error Classification Tests ------------------------------------------------

    [Fact]
    public async Task Collection_Failure_With_DNS_Error_Retries_Not_Fails()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        // Simulate DNS failure: HttpRequestException with null StatusCode and message containing "No such host"
        ctx.Handler.Responder = _ =>
        {
            throw new HttpRequestException("No such host is known", new OperationCanceledException("DNS resolution timeout"));
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        var result = await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        // Transient DNS error -> should retry, not fail immediately
        Assert.NotEqual(TikTokBatchSyncJobStatus.Failed, job.Status);
        Assert.Equal(1, job.AttemptCount); // First attempt failed
        Assert.NotNull(job.LastError);
        Assert.Contains("DNS or network", job.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(job.NextAttemptAt); // Retry scheduled
    }

    [Fact]
    public async Task Collection_Failure_With_HTTP_400_Classifies_As_Permanent()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        ctx.Handler.Responder = _ =>
        {
            throw new HttpRequestException("Bad request", null, HttpStatusCode.BadRequest);
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        // 400 = permanent, should fail immediately (AttemptCount >= MaxAttempts after 1 attempt)
        Assert.Equal(TikTokBatchSyncJobStatus.Failed, job.Status);
        Assert.True(job.AttemptCount >= 1);
    }

    [Fact]
    public async Task Collection_Failure_With_HTTP_429_Sets_NextAttemptAt_For_Retry()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        int attemptCount = 0;
        ctx.Handler.Responder = _ =>
        {
            attemptCount++;
            throw new HttpRequestException("Rate limited", null, HttpStatusCode.TooManyRequests);
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        var result = await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        // Rate limit (429) should trigger retry logic
        Assert.Equal(1, job.AttemptCount);
        Assert.NotEqual(TikTokBatchSyncJobStatus.Failed, job.Status);
        Assert.NotNull(job.NextAttemptAt); // Retry scheduled for future
        Assert.True(job.NextAttemptAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Collection_Failure_AttemptCount_Increments_On_Retry()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        int attempts = 0;
        ctx.Handler.Responder = _ =>
        {
            attempts++;
            throw new HttpRequestException("Network error", null, null);
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        // Process multiple times to simulate retries
        for (int i = 0; i < 3; i++)
        {
            var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
            if (job.Status == TikTokBatchSyncJobStatus.Failed) break;

            // Bring the parked retry due (keep NextAttemptAt non-null so the job stays parked).
            job.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            await ctx.Db.SaveChangesAsync();

            await ctx.Service.ProcessNextJobAsync();
        }

        var finalJob = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.True(finalJob.AttemptCount > 1);
    }

    [Fact]
    public async Task Error_Message_Does_Not_Contain_Credentials()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        ctx.Handler.Responder = _ =>
        {
            throw new HttpRequestException("Error with token: secrettoken123", null, HttpStatusCode.InternalServerError);
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.NotNull(job.LastError);
        // Verify that credentials are sanitized (no token leak)
        Assert.DoesNotContain("secrettoken123", job.LastError);
    }

    [Fact]
    public async Task Collection_Empty_Response_Valid_Does_Not_Retry()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(BatchSyncTestContext.EmptyListJson());

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        // Empty but valid response -> collection completed successfully
        Assert.True(job.CollectionCompleted);
        Assert.Equal(0, job.TotalVideoIdsFound);
        // Should NOT have retry scheduled
        Assert.Null(job.NextAttemptAt);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, job.Status);
    }

    // ---- NextAttemptAt Backoff Tests ------------------------------------------------

    [Fact]
    public async Task Retry_Sets_NextAttemptAt_In_Future()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        var beforeTime = DateTime.UtcNow;
        ctx.Handler.Responder = _ => throw new HttpRequestException("Transient", null, null);

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.NotNull(job.NextAttemptAt);
        Assert.True(job.NextAttemptAt > beforeTime);
    }

    [Fact]
    public async Task Permanent_Error_Does_Not_Set_NextAttemptAt()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        ctx.Handler.Responder = _ => throw new HttpRequestException("Auth failed", null, HttpStatusCode.Unauthorized);

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        // Permanent error -> job failed, no retry scheduled
        Assert.Equal(TikTokBatchSyncJobStatus.Failed, job.Status);
        Assert.Null(job.NextAttemptAt);
    }

    // ---- timezone-aware display ----------------------------------------------------

    [Fact]
    public async Task Status_Display_Times_Are_Rendered_In_Shop_Timezone_With_Label()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();
        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        // 08:30 UTC == 15:30 Asia/Jakarta (GMT+7). No manual offset anywhere.
        var job = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        job.NextAttemptAt = new DateTime(2026, 10, 14, 8, 30, 0, DateTimeKind.Utc);
        await ctx.Db.SaveChangesAsync();

        var status = await ctx.Service.GetStatusAsync(jobId);

        Assert.NotNull(status);
        Assert.Equal("Asia/Jakarta", status!.TimeZoneId);
        Assert.Equal("GMT+7", status.TimeZoneLabel);
        Assert.NotNull(status.NextAttemptAtDisplay);
        Assert.Contains("15:30", status.NextAttemptAtDisplay);
        Assert.Contains("GMT+7", status.NextAttemptAtDisplay);
        Assert.NotNull(status.CreatedAtDisplay);
    }

    // ---- parked retry becomes claimable after NextAttemptAt ---------------------------

    [Fact]
    public async Task Parked_Job_Resumes_After_NextAttemptAt_And_Completes()
    {
        using var ctx = new BatchSyncTestContext();
        var shop = await ctx.AddShopAsync();

        var listCalls = 0;
        ctx.Handler.Responder = request =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/shop_videos/performance"))
                return FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson());
            listCalls++;
            if (listCalls == 1)
                throw new HttpRequestException("No such host is known. (open-api.tiktokglobalshop.com:443)", null);
            return FakeTikTokApiHandler.Json(BatchSyncTestContext.EmptyListJson());
        };

        var jobId = await ctx.Service.StartJobAsync(shop.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));
        await ctx.Service.ProcessNextJobAsync();

        // First pass: DNS failure -> parked with honest retry state; collection NOT complete.
        var parked = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(TikTokBatchSyncJobStatus.CollectingIds, parked.Status);
        Assert.False(parked.CollectionCompleted);
        Assert.Equal(1, parked.AttemptCount);
        Assert.NotNull(parked.NextAttemptAt);
        Assert.Equal(1, listCalls);

        // A worker pass BEFORE NextAttemptAt must not touch the parked job.
        parked.NextAttemptAt = DateTime.UtcNow.AddMinutes(5);
        await ctx.Db.SaveChangesAsync();
        Assert.Null(await ctx.Service.ProcessNextJobAsync());
        Assert.Equal(1, listCalls);

        // Once due, the SAME parked job is claimable and resumes (no new job, no reset).
        parked = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        parked.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        await ctx.Db.SaveChangesAsync();
        await ctx.Service.ProcessNextJobAsync();

        var finished = await ctx.Db.TikTokBatchSyncJobs.SingleAsync(j => j.Id == jobId);
        Assert.True(finished.CollectionCompleted);
        Assert.Equal(TikTokBatchSyncJobStatus.Completed, finished.Status);
        Assert.Equal(0, finished.AttemptCount); // reset after success
        Assert.Equal(2, listCalls);
        Assert.Equal(1, await ctx.Db.TikTokBatchSyncJobs.CountAsync());
    }
}
