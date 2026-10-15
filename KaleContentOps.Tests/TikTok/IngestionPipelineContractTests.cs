using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using KaleContentOps.Controllers;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using KaleContentOps.ViewModels;
using KaleContentOps.Tests;

namespace KaleContentOps.TikTok.Tests
{
    // ================================================================================
    // Ingestion contract tests: REAL payloads -> HTTP (stubbed) -> parser -> DTO ->
    // ContentMetric persistence -> Content Log projection.
    //
    // The payloads are NOT fabricated: they are read at runtime from the git-tracked
    // evidence files in this repository:
    //   - tiktok_response.txt (== result_json_videoId_7687996887235890452.json)
    //       GET /analytics/202509/shop_videos/7687996887235890452/performance
    //   - KaleContentOps/App_Data/tiktok-api-sample.json (data.videos[])
    //       GET /analytics/202605/shop_videos/performance
    //
    // Field availability proven by these files (and pinned here):
    //   LIST   response carries views + commerce only - NO engagement, NO demographics.
    //   DETAILS response carries traffic (views/likes/comments/shares/new_followers)
    //          + viewer_profile (VIEWERS and NEW_FOLLOWER) - NO reach, NO watch metrics,
    //          and its sales block is window-scoped (list sync owns commerce fields).
    //
    // No production TikTok call is ever made: every HTTP call is served by an in-memory
    // handler, and the database is EF Core InMemory.
    // ================================================================================
    public class IngestionPipelineContractTests
    {
        // ------------------------------------------------------------------
        // Real payload loaders (fail loudly when the evidence files are absent)
        // ------------------------------------------------------------------

        private static string FindRepoFile(params string[] relativeSegments)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; depth < 12 && dir != null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, Path.Combine(relativeSegments));
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }
            throw new FileNotFoundException(
                $"Could not locate {string.Join('/', relativeSegments)} above the test output directory. " +
                "These tests read the REAL, git-tracked TikTok payloads and must run from within the repository.");
        }

        private static string? TryFindRepoFile(params string[] relativeSegments)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; depth < 12 && dir != null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, Path.Combine(relativeSegments));
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }
            return null;
        }

        /// <summary>Verbatim wire response of GET /analytics/202509/shop_videos/{id}/performance.</summary>
        private static string RealDetailsWireJson()
        {
            // Candidates are content-validated: the repository root ALSO contains an unrelated
            // UTF-16 file named tiktok_response.txt ("Using launch ...") that must never be
            // mistaken for the API capture. Both candidates below are git-tracked and identical.
            string?[] candidates =
            {
                TryFindRepoFile("KaleContentOps", "tiktok_response.txt"),
                TryFindRepoFile("result_json_videoId_7687996887235890452.json")
            };
            foreach (var text in candidates)
            {
                if (text != null
                    && text.Contains("\"performance\"", StringComparison.Ordinal)
                    && text.Contains("\"viewer_profile\"", StringComparison.Ordinal))
                {
                    return text;
                }
            }
            throw new FileNotFoundException(
                "Could not locate the real details capture (KaleContentOps/tiktok_response.txt or " +
                "result_json_videoId_7687996887235890452.json) above the test output directory.");
        }

        /// <summary>
        /// List wire response built from the FIRST element of the tracked fixture array
        /// (App_Data/tiktok-api-sample.json is data.videos[]), wrapped in the standard
        /// { code, message, data: { videos: [...] } } envelope - values verbatim.
        /// </summary>
        private static string ListWireJson()
        {
            var fixture = FindRepoFile("KaleContentOps", "App_Data", "tiktok-api-sample.json");
            using var doc = JsonDocument.Parse(fixture);
            var firstVideo = doc.RootElement[0].GetRawText();
            return $"{{\"code\":0,\"message\":\"Success\",\"data\":{{\"videos\":[{firstVideo}]}}}}";
        }

        // ------------------------------------------------------------------
        // Minimal synthetic case for the NoEnrichment branch ONLY. Deliberately not
        // presented as a TikTok capture: it exists to reach "200 + data + nothing
        // storable" so the bounded diagnostic can be asserted.
        // ------------------------------------------------------------------
        private const string NothingStorableJson =
            """{ "code": 0, "data": { "performance": { "intervals": [ { "traffic": {} } ], "viewer_profile": [] } } }""";

        // ------------------------------------------------------------------
        // Test doubles (same harness shape as DetailsSyncPersistenceE2ETests)
        // ------------------------------------------------------------------

        private sealed class StaticHandler : HttpMessageHandler
        {
            private readonly string _json;
            public StaticHandler(string json) => _json = json;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_json, Encoding.UTF8, "application/json")
                });
        }

        /// <summary>
        /// Serves the list payload for GET .../shop_videos/performance and the details
        /// payload for GET .../shop_videos/{video_id}/performance - still 100% fake HTTP.
        /// </summary>
        private sealed class RoutingHandler : HttpMessageHandler
        {
            private readonly string _listJson;
            private readonly string _detailsJson;
            public RoutingHandler(string listJson, string detailsJson)
            {
                _listJson = listJson;
                _detailsJson = detailsJson;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var json = path.EndsWith("/shop_videos/performance", StringComparison.Ordinal)
                    ? _listJson
                    : _detailsJson;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
        }

        private sealed class StubAuth : ITikTokAuthService
        {
            public Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
            public Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
            public Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
            public Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("TOKEN");
        }

        /// <summary>Captures formatted log messages so the bounded diagnostic can be asserted.</summary>
        private sealed class CollectingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));
        }

        private static AppDbContext CreateDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        private static TikTokVideoService CreateVideoService(AppDbContext db, HttpClient http) =>
            new(
                new SimpleHttpClientFactory(http),
                Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" }),
                db,
                new StubAuth(),
                new TikTokSignatureService(Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" })));

        private static HttpClient CreateJsonClient(HttpMessageHandler handler) =>
            new(handler) { BaseAddress = new Uri("https://open-api.tiktokglobalshop.com") };

        private static TikTokDetailsSyncService CreateDetailsService(AppDbContext db, TikTokVideoService videoSvc, HttpClient http, ILogger<TikTokDetailsSyncService>? logger = null) =>
            new(
                new SimpleHttpClientFactory(http),
                Options.Create(new TikTokOptions { AppKey = "K", AppSecret = "S" }),
                db,
                videoSvc,
                logger ?? NullLogger<TikTokDetailsSyncService>.Instance);

        // ===================================================================
        // 1. Details parse: every field the REAL payload offers is parsed,
        //    everything else stays NULL (no fabrication).
        // ===================================================================
        [Fact]
        public void Real_Details_Payload_Parses_Traffic_And_Both_Profiles_Without_Fabricating()
        {
            using var doc = JsonDocument.Parse(RealDetailsWireJson());
            var data = doc.RootElement.GetProperty("data");

            // Real service instance through its real constructor (no uninitialized-object tricks)
            using var db = CreateDb();
            var svc = CreateVideoService(db, CreateJsonClient(new StaticHandler(RealDetailsWireJson())));
            var dm = svc.ParseDetailsMetrics(data);

            // traffic: exactly comments/likes/new_followers/shares/views in the real capture
            Assert.Equal(477, dm.Views);
            Assert.Equal(15, dm.Likes);
            Assert.Equal(0, dm.Comments);      // 0 is an observation, not an absence
            Assert.Equal(0, dm.Shares);
            Assert.Equal(1, dm.NewFollowers);

            // The real capture has NO reach / watch metrics -> must stay null, never fabricated
            Assert.Null(dm.Reach);
            Assert.Null(dm.AverageWatch);
            Assert.Null(dm.FullWatchRate);

            // VIEWERS population (male 0.684 / female 0.316 - NOT the NEW_FOLLOWER values)
            Assert.Equal(0.684m, dm.Male);
            Assert.Equal(0.316m, dm.Female);
            Assert.Null(dm.NoGender);           // payload has no no_gender entry
            Assert.Equal(0.278m, dm.Age18);
            Assert.Equal(0.500m, dm.Age25);
            Assert.Equal(0.167m, dm.Age35);
            Assert.Equal(0.056m, dm.Age45);
            Assert.Null(dm.Age55);              // VIEWERS profile has no 55+ bucket here
            Assert.NotNull(dm.Countries);
            Assert.Single(dm.Countries!);       // blank country code skipped
            Assert.Equal(1.000m, dm.Countries!["ID"]);

            // NEW_FOLLOWER population, including the real "55 and above" label alias
            Assert.Equal(0.543m, dm.NfMale);
            Assert.Equal(0.457m, dm.NfFemale);
            Assert.Null(dm.NfNoGender);
            Assert.Equal(0.137m, dm.NfAge18);
            Assert.Equal(0.636m, dm.NfAge25);
            Assert.Equal(0.176m, dm.NfAge35);
            Assert.Equal(0.037m, dm.NfAge45);
            Assert.Equal(0.012m, dm.NfAge55);   // parsed from "55 and above"
            Assert.NotNull(dm.NfCountries);
            Assert.Equal(2, dm.NfCountries!.Count); // "" skipped; MY + ID kept
            Assert.Equal(0.007m, dm.NfCountries!["MY"]);
            Assert.Equal(0.990m, dm.NfCountries!["ID"]);
        }

        // ===================================================================
        // 2. Mapping + persistence: engagement and BOTH demographics JSONs land
        //    in the entity and survive SaveChanges; unsupported fields stay NULL;
        //    0 stays 0; the details row carries no commerce (list owns it).
        // ===================================================================
        [Fact]
        public async Task Real_Details_Payload_Maps_And_Persists_Engagement_And_Both_Demographics()
        {
            var db = CreateDb();
            var log = new ContentLog { VideoId = "7687996887235890452" };
            db.ContentLogs.Add(log);
            await db.SaveChangesAsync();

            using var doc = JsonDocument.Parse(RealDetailsWireJson());
            var data = doc.RootElement.GetProperty("data");

            var svc = CreateVideoService(db, CreateJsonClient(new StaticHandler(RealDetailsWireJson())));
            var dm = svc.ParseDetailsMetrics(data);
            var metric = svc.MapDetailsMetricsToContentMetric(dm, log.Id);
            Assert.NotNull(metric);

            db.ContentMetrics.Add(metric!);
            await db.SaveChangesAsync();

            var saved = await db.ContentMetrics.AsNoTracking().SingleAsync();
            Assert.Equal(log.Id, saved.ContentLogId);

            // Engagement available in the response is stored...
            Assert.Equal(477, saved.Views);
            Assert.Equal(15, saved.Likes);
            Assert.Equal(0, saved.Comments);
            Assert.Equal(0, saved.Shares);
            Assert.Equal(1, saved.NewFollowers);
            Assert.True(saved.Comments.HasValue); // 0 != null after round-trip

            // ...fields NOT offered by this endpoint stay NULL (never fabricated)...
            Assert.Null(saved.Reach);
            Assert.Null(saved.AverageWatch);
            Assert.Null(saved.FullWatchRate);

            // ...and the details snapshot carries NO commerce / date-range fields
            // (the list sync is their single producer - see ContentMetric.cs).
            Assert.Null(saved.GmvAmount);
            Assert.Null(saved.GmvCurrency);
            Assert.Null(saved.ItemsSold);
            Assert.Null(saved.SkuOrders);
            Assert.Null(saved.AvgCustomers);
            Assert.Null(saved.ClickThroughRate);
            Assert.Null(saved.HashtagsJson);
            Assert.Null(saved.ProductsJson);
            Assert.Null(saved.MetricStartDate);
            Assert.Null(saved.MetricEndDate);

            // VIEWERS demographics JSON with exact captured values; no fabricated buckets
            Assert.NotNull(saved.DemographicsJson);
            using (var demoDoc = JsonDocument.Parse(saved.DemographicsJson!))
            {
                var demo = demoDoc.RootElement;
                Assert.Equal(0.684m, demo.GetProperty("male").GetDecimal());
                Assert.Equal(0.316m, demo.GetProperty("female").GetDecimal());
                Assert.False(demo.TryGetProperty("no_gender", out _));
                var ages = demo.GetProperty("ages");
                Assert.Equal(0.278m, ages.GetProperty("18-24").GetDecimal());
                Assert.Equal(0.500m, ages.GetProperty("25-34").GetDecimal());
                Assert.Equal(0.167m, ages.GetProperty("35-44").GetDecimal());
                Assert.Equal(0.056m, ages.GetProperty("45-54").GetDecimal());
                Assert.False(ages.TryGetProperty("55+", out _));   // absent in VIEWERS
                Assert.False(ages.TryGetProperty("13-17", out _)); // unknown bucket never fabricated
                var countries = demo.GetProperty("countries");
                Assert.Single(countries.EnumerateObject());
                Assert.Equal(1.000m, countries.GetProperty("ID").GetDecimal());
            }

            // NEW_FOLLOWER demographics live in their OWN column, exact captured values
            Assert.NotNull(saved.NewFollowerDemographicsJson);
            using (var nfDoc = JsonDocument.Parse(saved.NewFollowerDemographicsJson!))
            {
                var nf = nfDoc.RootElement;
                Assert.Equal(0.543m, nf.GetProperty("male").GetDecimal());
                Assert.Equal(0.457m, nf.GetProperty("female").GetDecimal());
                Assert.False(nf.TryGetProperty("no_gender", out _));
                var ages = nf.GetProperty("ages");
                Assert.Equal(0.137m, ages.GetProperty("18-24").GetDecimal());
                Assert.Equal(0.636m, ages.GetProperty("25-34").GetDecimal());
                Assert.Equal(0.176m, ages.GetProperty("35-44").GetDecimal());
                Assert.Equal(0.037m, ages.GetProperty("45-54").GetDecimal());
                Assert.Equal(0.012m, ages.GetProperty("55+").GetDecimal()); // from "55 and above"
                Assert.False(ages.TryGetProperty("13-17", out _));
                var countries = nf.GetProperty("countries");
                Assert.Equal(2, countries.EnumerateObject().Count()); // blank code skipped
                Assert.Equal(0.007m, countries.GetProperty("MY").GetDecimal());
                Assert.Equal(0.990m, countries.GetProperty("ID").GetDecimal());
            }
        }

        // ===================================================================
        // 3. Details end-to-end over fake HTTP: the real wire response flows
        //    through RunDetailsSyncAsync and produces the persisted row.
        // ===================================================================
        [Fact]
        public async Task Details_Sync_EndToEnd_Http_Persists_The_Real_Payload_Row()
        {
            var db = CreateDb();
            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new TikTokShop { ShopCipher = "SHOP-D" };
            db.TikTokShops.Add(shop);
            db.ContentLogs.Add(new ContentLog { VideoId = "7687996887235890452", TikTokShop = shop });
            await db.SaveChangesAsync();

            var http = CreateJsonClient(new StaticHandler(RealDetailsWireJson()));
            var videoSvc = CreateVideoService(db, http);
            var detailsSvc = CreateDetailsService(db, videoSvc, http);

            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP-D", limit: 10);
            Assert.Equal(1, processed);

            var metric = await db.ContentMetrics.AsNoTracking().SingleAsync();
            Assert.Equal(477, metric.Views);
            Assert.Equal(15, metric.Likes);
            Assert.Equal(0, metric.Comments);
            Assert.Equal(0, metric.Shares);
            Assert.Equal(1, metric.NewFollowers);
            Assert.Null(metric.Reach);            // not offered by this endpoint
            Assert.Null(metric.AverageWatch);
            Assert.Null(metric.FullWatchRate);
            Assert.NotNull(metric.DemographicsJson);
            Assert.NotNull(metric.NewFollowerDemographicsJson);
        }

        // ===================================================================
        // 4. List end-to-end over fake HTTP: views + commerce + date range are
        //    persisted; engagement and demographics stay NULL (the list
        //    endpoint does not carry them - fixture-proven).
        // ===================================================================
        [Fact]
        public async Task List_Fixture_EndToEnd_Http_Persists_Views_And_Commerce_Only()
        {
            var db = CreateDb();
            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new TikTokShop { ShopCipher = "SHOP-L" };
            db.TikTokShops.Add(shop);
            await db.SaveChangesAsync();

            var http = CreateJsonClient(new StaticHandler(ListWireJson()));
            var videoSvc = CreateVideoService(db, http);

            var page = await videoSvc.FetchVideoListPageAsync("SHOP-L", "2026-10-13", "2026-10-16", null, CancellationToken.None);
            Assert.Equal("7631544267172842773", Assert.Single(page.VideoIds));

            await videoSvc.ApplyVideoListPageAsync("SHOP-L", page, CancellationToken.None);

            var log = await db.ContentLogs.AsNoTracking().SingleAsync();
            Assert.Equal("7631544267172842773", log.VideoId);
            Assert.Equal(shop.Id, log.TikTokShopId);
            Assert.Equal("sumpah lembut bgt ni kaos @Kale ", log.Title);
            Assert.Equal("pengengondrongs", log.Username);
            Assert.Equal(22, log.Duration);
            Assert.Equal(new DateTime(2026, 4, 22, 18, 29, 30), log.VideoPostTime);

            var metric = await db.ContentMetrics.AsNoTracking().SingleAsync();

            // Views + commerce, verbatim from the fixture
            Assert.Equal(356329, metric.Views);
            Assert.Equal(100655055.00m, metric.GmvAmount);
            Assert.Equal("IDR", metric.GmvCurrency);
            Assert.Equal(537, metric.ItemsSold);
            Assert.Equal(530, metric.SkuOrders);
            Assert.Equal(446m, metric.AvgCustomers);
            Assert.Equal(0.0637m, metric.ClickThroughRate);
            Assert.Equal("""["kale"]""", metric.HashtagsJson);
            Assert.NotNull(metric.ProductsJson);
            using (var products = JsonDocument.Parse(metric.ProductsJson!))
            {
                var p = Assert.Single(products.RootElement.EnumerateArray());
                Assert.Equal("1731469947016938789", p.GetProperty("id").GetString());
                Assert.Equal("Kale Juno T-Shirt Cotton Cloudy Asian Fit", p.GetProperty("name").GetString());
            }

            // The batch date range is recorded on the list snapshot
            Assert.Equal(new DateTime(2026, 10, 13), metric.MetricStartDate);
            Assert.Equal(new DateTime(2026, 10, 16), metric.MetricEndDate);

            // Engagement + demographics are NOT carried by the list endpoint -> NULL, not fabricated
            Assert.Null(metric.Reach);
            Assert.Null(metric.Likes);
            Assert.Null(metric.Comments);
            Assert.Null(metric.Shares);
            Assert.Null(metric.NewFollowers);
            Assert.Null(metric.AverageWatch);
            Assert.Null(metric.FullWatchRate);
            Assert.Null(metric.DemographicsJson);
            Assert.Null(metric.NewFollowerDemographicsJson);
        }

        // ===================================================================
        // 5. List THEN details over fake HTTP: details sync APPENDS its snapshot
        //    and never rewrites the list row; the Content Log then shows the
        //    union of both producers (the exact production scenario reported
        //    for ContentLogId 36059-36068 once their details sync runs).
        // ===================================================================
        [Fact]
        public async Task List_Then_Details_Sync_Appends_A_New_Snapshot_And_Content_Log_Shows_The_Union()
        {
            var db = CreateDb();
            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new TikTokShop { ShopCipher = "SHOP-U" };
            db.TikTokShops.Add(shop);
            await db.SaveChangesAsync();

            var http = CreateJsonClient(new RoutingHandler(ListWireJson(), RealDetailsWireJson()));
            var videoSvc = CreateVideoService(db, http);

            // Phase A: list sync creates the views+commerce snapshot
            var page = await videoSvc.FetchVideoListPageAsync("SHOP-U", "2026-10-13", "2026-10-16", null, CancellationToken.None);
            await videoSvc.ApplyVideoListPageAsync("SHOP-U", page, CancellationToken.None);
            Assert.Equal(1, await db.ContentMetrics.CountAsync());

            // Phase B: details sync (same shop) appends the engagement+demographics snapshot
            var detailsSvc = CreateDetailsService(db, videoSvc, http);
            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP-U", limit: 10);
            Assert.Equal(1, processed);

            // Exactly TWO rows: nothing was updated, nothing was deleted
            var rows = await db.ContentMetrics.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
            Assert.Equal(2, rows.Count);

            // The list row is untouched by the details sync (append-only ingestion)
            var listRow = rows[0];
            Assert.Equal(356329, listRow.Views);
            Assert.Equal(100655055.00m, listRow.GmvAmount);
            Assert.Equal("IDR", listRow.GmvCurrency);
            Assert.Equal(537, listRow.ItemsSold);
            Assert.Equal("""["kale"]""", listRow.HashtagsJson);
            Assert.NotNull(listRow.ProductsJson);
            Assert.Equal(new DateTime(2026, 10, 13), listRow.MetricStartDate);
            Assert.Null(listRow.Likes);              // list rows never carry engagement
            Assert.Null(listRow.DemographicsJson);

            // The appended details row carries engagement + demographics, no commerce
            var detailsRow = rows[1];
            Assert.Equal(477, detailsRow.Views);
            Assert.Equal(15, detailsRow.Likes);
            Assert.Equal(0, detailsRow.Comments);
            Assert.Equal(0, detailsRow.Shares);
            Assert.Equal(1, detailsRow.NewFollowers);
            Assert.NotNull(detailsRow.DemographicsJson);
            Assert.NotNull(detailsRow.NewFollowerDemographicsJson);
            Assert.Null(detailsRow.GmvAmount);        // sales block is window-scoped; list owns commerce
            Assert.Null(detailsRow.MetricStartDate);

            // Content Log projection shows the UNION of both snapshots
            var controller = new ContentLogController(db);
            var result = await controller.Index(null, null);
            var view = Assert.IsType<ViewResult>(result);
            var vm = Assert.IsType<ContentLogIndexViewModel>(view.Model);
            var item = Assert.Single(vm.Items);

            // From the details (newest) snapshot
            Assert.Equal(477, item.Views);
            Assert.Equal(15, item.Likes);
            Assert.Equal(0, item.Comments);
            Assert.Equal(0, item.Shares);
            Assert.Equal(1, item.NewFollowers);
            Assert.Equal(0.684m, item.Male);          // VIEWERS demographics
            Assert.Equal(0.316m, item.Female);

            // From the list snapshot - values that a newer details row must NOT blank
            Assert.Equal(100655055.00m, item.GmvAmount);
            Assert.Equal("IDR", item.GmvCurrency);
            Assert.Equal(537, item.ItemsSold);
            Assert.Equal(530, item.SkuOrders);
            Assert.Equal(0.0637m, item.ClickThroughRate);
            Assert.NotNull(item.Hashtags);
            Assert.Equal("kale", Assert.Single(item.Hashtags!));
            Assert.NotNull(item.Products);
            Assert.Equal("1731469947016938789", Assert.Single(item.Products!).Id);

            // Not offered by either endpoint for this capture
            Assert.Null(item.Reach);
            Assert.Null(item.AverageWatch);
            Assert.Null(item.FullWatchRate);
        }

        // ===================================================================
        // 6. Bounded diagnostic: a 200 response that yields NOTHING storable now
        //    emits one Information line listing which sections were present -
        //    field names only, never the access token or response values.
        // ===================================================================
        [Fact]
        public async Task NoEnrichment_Logs_Bounded_Field_Presence_Without_Credentials()
        {
            var db = CreateDb();
            db.TikTokCredentials.Add(new TikTokCredential { AppKey = "K" });
            var shop = new TikTokShop { ShopCipher = "SHOP-N" };
            db.TikTokShops.Add(shop);
            db.ContentLogs.Add(new ContentLog { VideoId = "V-NO-STUFF", TikTokShop = shop });
            await db.SaveChangesAsync();

            var http = CreateJsonClient(new StaticHandler(NothingStorableJson));
            var videoSvc = CreateVideoService(db, http);
            var logger = new CollectingLogger<TikTokDetailsSyncService>();
            var detailsSvc = CreateDetailsService(db, videoSvc, http, logger);

            var processed = await detailsSvc.RunDetailsSyncAsync("SHOP-N", limit: 10);

            Assert.Equal(0, processed);                       // no row was storable
            Assert.Equal(0, await db.ContentMetrics.CountAsync()); // nothing written, nothing fabricated

            var entry = Assert.Single(logger.Entries, e =>
                e.Message.Contains("no storable metric", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Contains("traffic=[] profiles=[]", entry.Message, StringComparison.Ordinal);

            // Credential-free by construction: the stubbed access token must never appear
            Assert.DoesNotContain("TOKEN", entry.Message, StringComparison.Ordinal);
            // The NoEnrichment path is informational, not an error
            Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        }
    }
}
