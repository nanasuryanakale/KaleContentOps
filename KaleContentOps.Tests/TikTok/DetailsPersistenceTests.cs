using System;
using System.Text.Json;
using System.Runtime.Serialization;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using Xunit;
using KaleContentOps.Services.TikTok;
using KaleContentOps.Data;
using KaleContentOps.Models;

namespace KaleContentOps.TikTok.Tests
{
    public class DetailsPersistenceTests
    {
        [Fact]
        public async Task ParseAndPersist_ContentMetric_Should_Create_Row_With_Demographics()
        {
            var sample = "{\"code\":0,\"data\":{\"performance\":{\"intervals\":[{\"traffic\":{\"views\":23,\"likes\":0,\"comments\":0,\"shares\":0,\"new_followers\":0}}],\"viewer_profile\":[{\"type\":\"VIEWERS\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.500\"},{\"gender\":\"female\",\"percentage\":\"0.500\"}],\"age_distribution\":[{\"age\":\"18-24\",\"percentage\":\"0.500\"},{\"age\":\"25-34\",\"percentage\":\"0.250\"},{\"age\":\"35-44\",\"percentage\":\"0.250\"}]},{\"type\":\"NEW_FOLLOWER\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.600\"}],\"age_distribution\":[{\"age\":\"25-34\",\"percentage\":\"0.600\"}]}]}}}";

            using var doc = JsonDocument.Parse(sample);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

            // Create DbContext with InMemory provider
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: "DetailsPersistenceTestDb")
                .Options;

            using (var db = new AppDbContext(options))
            {
                // seed a ContentLog to reference
                var cl = new ContentLog { VideoId = "vid-test-1" };
                db.ContentLogs.Add(cl);
                db.SaveChanges();

                // Call parser and mapper via uninitialized service (methods are instance but do not require constructor state)
                var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
                var svc = (TikTokVideoService)svcObj;

                var dm = svc.ParseDetailsMetrics(data);
                var metric = svc.MapDetailsMetricsToContentMetric(dm, cl.Id);

                Assert.NotNull(metric);
                db.ContentMetrics.Add(metric);
                db.SaveChanges();

                var saved = await db.ContentMetrics.FirstOrDefaultAsync();
                Assert.NotNull(saved);

                Assert.Equal(23, saved.Views);
                Assert.Equal(0, saved.Likes);
                Assert.Equal(0, saved.Comments);
                Assert.Equal(0, saved.Shares);Assert.Equal(0, saved.NewFollowers);
                Assert.NotNull(saved.DemographicsJson);
                Assert.Contains("\"male\":0.5", saved.DemographicsJson);
                Assert.Contains("\"female\":0.5", saved.DemographicsJson);
                Assert.Contains("\"18-24\":0.5", saved.DemographicsJson);
                Assert.Contains("\"25-34\":0.25", saved.DemographicsJson);
                Assert.Contains("\"35-44\":0.25", saved.DemographicsJson);
            }        }

            var sample = "{\"code\":0,\"data\":{\"performance\":{\"intervals\":[{\"traffic\":{\"views\":23,\"likes\":0,\"comments\":0,\"shares\":0,\"saves\":0,\"new_followers\":0}}],\"viewer_profile\":[{\"type\":\"VIEWERS\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.500\"},{\"gender\":\"female\",\"percentage\":\"0.500\"}],\"age_distribution\":[{\"age\":\"18-24\",\"percentage\":\"0.500\"},{\"age\":\"25-34\",\"percentage\":\"0.250\"},{\"age\":\"35-44\",\"percentage\":\"0.250\"}]},{\"type\":\"NEW_FOLLOWER\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.600\"}],\"age_distribution\":[{\"age\":\"25-34\",\"percentage\":\"0.600\"}]}]}}}";
            using var doc = JsonDocument.Parse(sample);            var root = doc.RootElement;            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;
            // Create DbContext with InMemory provider            var options = new DbContextOptionsBuilder<AppDbContext>()                .UseInMemoryDatabase(databaseName: "DetailsPersistenceTestDbSaves")                .Options;
            using (var db = new AppDbContext(options))            {                // seed a ContentLog to referencevar cl = new ContentLog { VideoId = "vid-test-saves-1" };
                db.ContentLogs.Add(cl);
                db.SaveChanges();
                // Call parser and mapper via uninitialized service (methods are instance but do not require constructor state)
                var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
                var svc = (TikTokVideoService)svcObj;
                var dm = svc.ParseDetailsMetrics(data);
                var metric = svc.MapDetailsMetricsToContentMetric(dm, cl.Id);
                Assert.NotNull(metric);
                db.ContentMetrics.Add(metric);
                db.SaveChanges();
                var saved = await db.ContentMetrics.FirstOrDefaultAsync();
                Assert.NotNull(saved);
// Saves should be preserved as explicit zero (present in payload)Assert.Equal(0, saved.Saves);
                Assert.NotNull(saved.SavesPath);
            }
        }
        
        [Fact]        public async Task ParseAndPersist_ContentMetric_Should_Preserve_Saves_Positive_Value()        {            var sample = "{\"code\":0,\"data\":{\"performance\":{\"intervals\":[{\"traffic\":{\"views\":23,\"likes\":0,\"comments\":0,\"shares\":0,\"saves\":5,\"new_followers\":0}}],\"viewer_profile\":[{\"type\":\"VIEWERS\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.500\"},{\"gender\":\"female\",\"percentage\":\"0.500\"}],\"age_distribution\":[{\"age\":\"18-24\",\"percentage\":\"0.500\"},{\"age\":\"25-34\",\"percentage\":\"0.250\"},{\"age\":\"35-44\",\"percentage\":\"0.250\"}]},{\"type\":\"NEW_FOLLOWER\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.600\"}],\"age_distribution\":[{\"age\":\"25-34\",\"percentage\":\"0.600\"}]}]}}}";            using var doc = JsonDocument.Parse(sample);            var root = doc.RootElement;            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;            // Create DbContext with InMemory provider            var options = new DbContextOptionsBuilder<AppDbContext>()                .UseInMemoryDatabase(databaseName: "DetailsPersistenceTestDbSavesPositive")                .Options;            using (var db = new AppDbContext(options))            {                // seed a ContentLog to reference                var cl = new ContentLog { VideoId = "vid-test-saves-positive-1" };                db.ContentLogs.Add(cl);                db.SaveChanges();                // Call parser and mapper via uninitialized service (methods are instance but do not require constructor state)                var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));                var svc = (TikTokVideoService)svcObj;                var dm = svc.ParseDetailsMetrics(data);                var metric = svc.MapDetailsMetricsToContentMetric(dm, cl.Id);                Assert.NotNull(metric);                db.ContentMetrics.Add(metric);                db.SaveChanges();                var saved = await db.ContentMetrics.FirstOrDefaultAsync();                Assert.NotNull(saved);                // Saves should be preserved as explicit positive value (present in payload)                Assert.Equal(5, saved.Saves);                Assert.NotNull(saved.SavesPath);            }        }        

        [Fact]        public async Task ParseAndPersist_ContentMetric_Should_Leave_Saves_Null_When_Absent()        {
            var sample = "{\"code\":0,\"data\":{\"performance\":{\"intervals\":[{\"traffic\":{\"views\":23,\"likes\":0,\"comments\":0,\"shares\":0,\"new_followers\":0}}],\"viewer_profile\":[{\"type\":\"VIEWERS\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.500\"},{\"gender\":\"female\",\"percentage\":\"0.500\"}],\"age_distribution\":[{\"age\":\"18-24\",\"percentage\":\"0.500\"},{\"age\":\"25-34\",\"percentage\":\"0.250\"},{\"age\":\"35-44\",\"percentage\":\"0.250\"}]},{\"type\":\"NEW_FOLLOWER\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.600\"}],\"age_distribution\":[{\"age\":\"25-34\",\"percentage\":\"0.600\"}]}]}}}";
            using var doc = JsonDocument.Parse(sample);            var root = doc.RootElement;            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;
            // Create DbContext with InMemory provider            var options = new DbContextOptionsBuilder<AppDbContext>()                .UseInMemoryDatabase(databaseName: "DetailsPersistenceTestDbSavesAbsent")                .Options;            using (var db = new AppDbContext(options))            {                // seed a ContentLog to reference                var cl = new ContentLog { VideoId = "vid-test-saves-absent-1" };                db.ContentLogs.Add(cl);                db.SaveChanges();
                // Call parser and mapper via uninitialized service (methods are instance but do not require constructor state)                var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));                var svc = (TikTokVideoService)svcObj;                var dm = svc.ParseDetailsMetrics(data);                var metric = svc.MapDetailsMetricsToContentMetric(dm, cl.Id);                Assert.NotNull(metric);
                db.ContentMetrics.Add(metric);                db.SaveChanges();                var saved = await db.ContentMetrics.FirstOrDefaultAsync();
                Assert.NotNull(saved);
                // Saves should be null when absent from API response (not 0, but null)
                Assert.Null(saved.Saves);
                Assert.Null(saved.SavesPath);
            }        }    }}


