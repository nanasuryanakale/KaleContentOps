using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using KaleContentOps.Services.TikTok;
using KaleContentOps.Tests.TikTok.BatchSync;
using Xunit;

namespace KaleContentOps.Tests.TikTok;

/// <summary>
/// TikTokVideoService.RunDetailsDiagnosticAsync now defaults to the announced replacement
/// endpoint GET /analytics/202609/shop_videos/batch_performance (retires the 202509
/// single-video endpoint on 2026-12-28). Verified contract (live probe 2026-10-22):
/// the id travels in the video_ids query parameter, the per-video payload is wrapped as
/// data.videos[{video_id, video, performance}], and windows outside the lookback are
/// rejected with code 28001022. These tests cover: request shape, matched-entry
/// unwrapping, honest per-video no-data, the ONE bounded legacy fallback for the
/// lookback rejection only, and a configured TikTok:DetailsPath staying untouched.
/// All HTTP is faked - no live TikTok call is ever made.
/// </summary>
public class DetailsBatchMigrationTests
{
    private const string BatchPath = "/analytics/202609/shop_videos/batch_performance";
    private const string LegacyPathFragment = "/analytics/202509/shop_videos/";

    /// <summary>Verified batch envelope wrapping the SAME per-video payload as 202509.</summary>
    private static string BatchJson(string videoId, long views = 263, int likes = 13)
        => $$"""
           {
             "code": 0,
             "message": "Success",
             "request_id": "req-batch-0001",
             "data": {
               "videos": [
                 {
                   "video_id": "{{videoId}}",
                   "video": { "id": "{{videoId}}" },
                   "performance": {
                     "intervals": [
                       {
                         "start_date": "2026-10-20",
                         "end_date": "2026-10-21",
                         "traffic": { "views": {{views}}, "likes": {{likes}}, "comments": 0, "shares": 0, "new_followers": 0 }
                       }
                     ],
                     "viewer_profile": []
                   }
                 }
               ]
             }
           }
           """;

    /// <summary>Verified shape of the lookback-window rejection observed live (code=200).</summary>
    private static string LookbackRejectionJson()
        => """{ "code": 28001022, "message": "The query date range is outside the lookback window", "request_id": "req-lookback" }""";

    private static async Task<(int? Status, string Endpoint, System.Text.Json.JsonElement? Root, System.Text.Json.JsonElement? Data)> RunDetails(
        BatchSyncTestContext ctx, string videoId = "V-1", string shopCipher = "SHOP-TEST")
    {
        var (status, endpoint, root, data, _) = await ((TikTokVideoService)ctx.VideoService)
            .RunDetailsDiagnosticAsync(videoId, shopCipher);
        return (status, endpoint, root, data);
    }

    /// <summary>The diagnostic returns the FULL request URL; assertions compare the path.</summary>
    private static string PathOf(string endpointOrUrl) => endpointOrUrl.Split('?')[0];

    // ------------------------------------------------------------------
    // Default endpoint: batch_performance with video_ids
    // ------------------------------------------------------------------

    [Fact]
    public async Task DefaultEndpoint_IsBatchPerformance_WithVideoIds_AndMatchedEntryUnwrapped()
    {
        using var ctx = new BatchSyncTestContext();
        ctx.Handler.Responder = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path == BatchPath
                ? FakeTikTokApiHandler.Json(BatchJson("V-1"))
                : FakeTikTokApiHandler.Json("{}", HttpStatusCode.NotFound);
        };

        var (status, endpoint, root, data) = await RunDetails(ctx);

        Assert.Equal(200, status);
        Assert.Equal(BatchPath, PathOf(endpoint));

        // Exactly ONE details request; identity in the video_ids query, not the path.
        Assert.Equal("details:V-1", Assert.Single(ctx.Handler.RequestLog));
        var query = Assert.Single(ctx.Handler.DetailsRequestQueries);
        Assert.Equal("V-1", query["video_ids"]);
        Assert.False(query.ContainsKey("video_id"));
        Assert.Equal("SHOP-TEST", query["shop_cipher"]);
        Assert.True(query.ContainsKey("start_date_ge"));
        Assert.True(query.ContainsKey("end_date_lt"));
        Assert.True(query.ContainsKey("sign"));
        Assert.True(query.ContainsKey("timestamp"));

        // Unwrapped MATCHED entry: the legacy parser sees data.performance unchanged.
        Assert.NotNull(root);
        Assert.NotNull(data);
        Assert.Equal("V-1", data!.Value.GetProperty("video_id").GetString());
        Assert.True(data.Value.TryGetProperty("performance", out _));
    }

    [Fact]
    public async Task BatchResponse_UsesVideoIdFromVideoIdsParameter_InBatchQuery()
    {
        using var ctx = new BatchSyncTestContext();
        ctx.Handler.Responder = request =>
            request.RequestUri!.AbsolutePath == BatchPath
                ? FakeTikTokApiHandler.Json(BatchJson("V-77", views: 555))
                : FakeTikTokApiHandler.Json("{}", HttpStatusCode.NotFound);

        var (_, endpoint, _, data) = await RunDetails(ctx, videoId: "V-77");

        Assert.Equal(BatchPath, PathOf(endpoint));
        Assert.Equal("V-77", ctx.Handler.DetailsRequestQueries.Single()["video_ids"]);
        Assert.NotNull(data);
        Assert.Equal(555, data!.Value.GetProperty("performance")
            .GetProperty("intervals")[0].GetProperty("traffic").GetProperty("views").GetInt64());
    }

    // ------------------------------------------------------------------
    // Per-video no-data and errors
    // ------------------------------------------------------------------

    [Fact]
    public async Task MissingVideoEntry_IsHonestNullData_NoFallback()
    {
        using var ctx = new BatchSyncTestContext();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(
            """{ "code": 0, "message": "Success", "request_id": "req-batch", "data": { "videos": [] } }""");

        var (status, endpoint, root, data) = await RunDetails(ctx, videoId: "V-NODATA");

        Assert.Equal(200, status);
        Assert.Equal(BatchPath, PathOf(endpoint));
        Assert.NotNull(root); // envelope was fine
        Assert.Null(data); // this video simply has no data - never another video's metrics
        Assert.Equal("details:V-NODATA", Assert.Single(ctx.Handler.RequestLog)); // exactly one request, no fallback
    }

    [Fact]
    public async Task OtherBusinessError_DoesNotFallBack()
    {
        using var ctx = new BatchSyncTestContext();
        // NOTE: 36009002 is TikTok's business rate-limit code and the shared retry
        // helper legitimately retries it BEFORE details parsing - an unrelated
        // business error (here) must not trigger the legacy endpoint switch.
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(
            """{ "code": 36009001, "message": "invalid video id", "request_id": "req-err" }""");

        var (status, endpoint, root, data) = await RunDetails(ctx);

        Assert.Equal(200, status);
        Assert.Equal(BatchPath, PathOf(endpoint));
        Assert.NotNull(root);
        Assert.Null(data); // business error is never forwarded as successful data
        Assert.Single(ctx.Handler.RequestLog); // no legacy fallback for unrelated errors
    }

    [Fact]
    public async Task Http400InvalidId_DoesNotFallBack()
    {
        using var ctx = new BatchSyncTestContext();
        ctx.Handler.Responder = _ => FakeTikTokApiHandler.Json(
            """{ "code": 36009004, "message": "invalid parameter", "request_id": "req-400" }""",
            HttpStatusCode.BadRequest);

        var (status, endpoint, _, data) = await RunDetails(ctx, videoId: "not-a-real-id");

        Assert.Equal(400, status);
        Assert.Equal(BatchPath, PathOf(endpoint));
        Assert.Null(data);
        Assert.Single(ctx.Handler.RequestLog); // envelope 400 = request problem, retrying on legacy is pointless
    }

    // ------------------------------------------------------------------
    // Bounded lookback fallback
    // ------------------------------------------------------------------

    [Fact]
    public async Task LookbackRejection_FallsBackToLegacyOnce_AndSucceeds()
    {
        using var ctx = new BatchSyncTestContext();
        ctx.Handler.Responder = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path == BatchPath
                ? FakeTikTokApiHandler.Json(LookbackRejectionJson())
                : path.Contains(LegacyPathFragment, StringComparison.Ordinal)
                    ? FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson(views: 42))
                    : FakeTikTokApiHandler.Json("{}", HttpStatusCode.NotFound);
        };

        var (status, endpoint, _, data) = await RunDetails(ctx, videoId: "V-HIST");

        Assert.Equal(200, status);
        Assert.Equal($"{LegacyPathFragment}V-HIST/performance", PathOf(endpoint));
        Assert.NotNull(data); // legacy payload parsed

        // Exactly two details requests: one batch attempt + one legacy retry.
        Assert.Equal(2, ctx.Handler.RequestLog.Count);
        Assert.All(ctx.Handler.RequestLog, entry => Assert.Equal("details:V-HIST", entry));

        var legacyQuery = ctx.Handler.DetailsRequestQueries[1];
        Assert.False(legacyQuery.ContainsKey("video_ids")); // legacy identity lives in the path
        Assert.True(legacyQuery.ContainsKey("start_date_ge")); // still fully signed with the window
        Assert.True(legacyQuery.ContainsKey("sign"));
    }

    [Fact]
    public async Task LegacyFallback_IsNotAttemptedTwice_WhenLegacyAlsoRejects()
    {
        using var ctx = new BatchSyncTestContext();
        ctx.Handler.Responder = request =>
            request.RequestUri!.AbsolutePath == BatchPath || request.RequestUri.AbsolutePath.Contains(LegacyPathFragment, StringComparison.Ordinal)
                ? FakeTikTokApiHandler.Json(LookbackRejectionJson())
                : FakeTikTokApiHandler.Json("{}", HttpStatusCode.NotFound);

        var (status, endpoint, _, data) = await RunDetails(ctx);

        Assert.Equal(200, status);
        Assert.Null(data);
        // Bounded: batch + ONE legacy attempt, never an endless endpoint ping-pong.
        Assert.Equal(2, ctx.Handler.RequestLog.Count);
        Assert.Equal($"{LegacyPathFragment}V-1/performance", PathOf(endpoint));
    }

    // ------------------------------------------------------------------
    // Rate limit and configured override
    // ------------------------------------------------------------------

    [Fact]
    public async Task RateLimit429_RetriesTheSameBatchEndpoint_NotLegacy()
    {
        using var ctx = new BatchSyncTestContext();
        var batchCalls = 0;
        ctx.Handler.Responder = request =>
        {
            if (request.RequestUri!.AbsolutePath != BatchPath)
            {
                return FakeTikTokApiHandler.Json("{}", HttpStatusCode.NotFound);
            }

            batchCalls++;
            return batchCalls == 1
                ? FakeTikTokApiHandler.Json(
                    """{ "code": 36009002, "message": "rate limit", "request_id": "req-429" }""",
                    HttpStatusCode.TooManyRequests, retryAfter: "0.1")
                : FakeTikTokApiHandler.Json(BatchJson("V-1"));
        };

        var (status, _, _, data) = await RunDetails(ctx);

        Assert.Equal(200, status);
        Assert.NotNull(data);
        Assert.Equal(2, batchCalls); // 429 retried the SAME endpoint - the limit is never masked by a silent switch
        Assert.All(ctx.Handler.RequestLog, entry => Assert.Equal("details:V-1", entry));
    }

    [Fact]
    public async Task ConfiguredDetailsPath_IsHonored_Verbatim_NoBatchNoFallback()
    {
        var options = new TikTokOptions
        {
            AppKey = "K",
            AppSecret = "S",
            BatchSyncRetryBaseSeconds = 0,
            DetailsPath = "/analytics/custom/shop_videos/{video_id}/performance"
        };
        using var ctx = new BatchSyncTestContext(options);
        ctx.Handler.Responder = request =>
            request.RequestUri!.AbsolutePath == "/analytics/custom/shop_videos/V-9/performance"
                ? FakeTikTokApiHandler.Json(BatchSyncTestContext.DetailsJson(views: 77))
                : FakeTikTokApiHandler.Json("{}", HttpStatusCode.NotFound);

        var (status, endpoint, _, data) = await RunDetails(ctx, videoId: "V-9");

        Assert.Equal(200, status);
        Assert.Equal("/analytics/custom/shop_videos/V-9/performance", PathOf(endpoint));
        Assert.NotNull(data);
        Assert.Single(ctx.Handler.RequestLog); // the user's configured endpoint is never second-guessed
        Assert.False(ctx.Handler.DetailsRequestQueries.Single().ContainsKey("video_ids"));
    }
}
