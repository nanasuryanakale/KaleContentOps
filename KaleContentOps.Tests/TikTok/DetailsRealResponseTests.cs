using System;
using System.Text.Json;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;
using KaleContentOps.Services.TikTok;
using KaleContentOps.Data;
using KaleContentOps.Models;

namespace KaleContentOps.TikTok.Tests
{
    // Regression tests based on the REAL API response of
    // GET /analytics/202509/shop_videos/7687996887235890452/performance
    // (evidence captured 2026-09-22; structure shown below is the actual payload shape)
    public class DetailsRealResponseTests
    {
        // Real response shape: data.performance.intervals[].traffic has exactly
        // comments/likes/new_followers/shares/views (no reach/watch time fields),
        // data.performance.intervals[].sales.overall has commerce fields,
        // and viewer_profile is an EMPTY ARRAY.
        private const string RealResponseJson = """
            {
              "code": 0,
              "message": "OK",
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "comments": 0,
                        "likes": 13,
                        "new_followers": 0,
                        "shares": 0,
                        "views": 263
                      },
                      "sales": {
                        "overall": {
                          "ctr": "0.0570",
                          "customers": 0,
                          "gmv": { "amount": "0.00", "currency": "IDR" },
                          "gpm": { "amount": "0.00", "currency": "IDR" },
                          "items_sold": 0,
                          "product_clicks": 15,
                          "product_impressions":  212
                        }
                      }
                    }
                  ],
                  "viewer_profile": []
                }
              }
            }
            """;

        private static DetailsMetrics ParseReal()
        {
            using var doc = JsonDocument.Parse(RealResponseJson);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;
            return svc.ParseDetailsMetrics(data);
        }

        [Fact]
        public void RealTraffic_Parses_Core_Engagement()
        {
            var dm = ParseReal();

            Assert.Equal(263, dm.Views);
            Assert.Equal(13, dm.Likes);
            Assert.Equal(0, dm.Comments);
            Assert.Equal(0, dm.Shares);
            Assert.Equal(0, dm.NewFollowers);
        }

        [Fact]
        public void RealResponse_Zero_Is_Not_Null()
        {
            var dm = ParseReal();

            // 0 = valid zero (present in payload), must NOT become null
            Assert.NotNull(dm.Comments);
            Assert.NotNull(dm.Shares);
            Assert.NotNull(dm.NewFollowers);
            Assert.Equal(0, dm.Comments!.Value);
            Assert.Equal(0, dm.Shares!.Value);
            Assert.Equal(0, dm.NewFollowers!.Value);
        }

        [Fact]
        public void RealResponse_UnverifiedFields_Remain_Null()
        {
            var dm = ParseReal();

            // Real payload has NO reach / watch-time / full-watch fields and empty viewer_profile:
            Assert.Null(dm.Reach);
            Assert.Null(dm.AverageWatch);
            Assert.Null(dm.FullWatchRate);
            Assert.Null(dm.Male);
            Assert.Null(dm.Female);
            Assert.Null(dm.NoGender);
            Assert.Null(dm.Age18);
            Assert.Null(dm.Age25);
            Assert.Null(dm.Age35);
            Assert.Null(dm.Age45);
            Assert.Null(dm.Age55);
        }

        [Fact]
        public async Task RealResponse_Maps_To_ContentMetric_ZeroPreserved_DemographicsNull()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);
            var cl = new ContentLog { VideoId = "7687996887235890452" };
            db.ContentLogs.Add(cl);
            await db.SaveChangesAsync();

            var dm = ParseReal();
            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;

            var metric = svc.MapDetailsMetricsToContentMetric(dm, cl.Id);

            Assert.NotNull(metric);
            Assert.Equal(263, metric!.Views);
            Assert.Equal(13, metric.Likes);
            Assert.Equal(0, metric.Comments);            // valid zero preserved in DB-bound entity
            Assert.Equal(0, metric.Shares);
            Assert.Equal(0, metric.NewFollowers);
            Assert.Null(metric.Reach);
            Assert.Null(metric.AverageWatch);
            Assert.Null(metric.FullWatchRate);
            Assert.Null(metric.DemographicsJson);        // viewer_profile [] -> demographics null
        }

        [Fact]
        public void RealResponse_Missing_Traffic_Field_Yields_Null()
        {
            // Same structure, but the real payload minus "shares" -> field must be null, not 0
            const string json = """
                {
                  "code": 0,
                  "data": {
                    "performance": {
                      "intervals": [
                        {
                          "traffic": {
                            "comments": 0,
                            "likes": 13,
                            "new_followers": 0,
                            "views": 263
                          }
                        }
                      ],
                      "viewer_profile": []
                    }
                  }
                }
                """;

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;
            var dm = svc.ParseDetailsMetrics(data);

            Assert.Equal(263, dm.Views);
            Assert.Equal(13, dm.Likes);
            Assert.Equal(0, dm.Comments);
            Assert.Null(dm.Shares);   // missing -> null (not 0)
            Assert.Equal(0, dm.NewFollowers);
        }

        [Fact]
        public void RealResponse_Multiple_Intervals_Are_Aggregated()
        {
            // intervals[] is an array in the real API. Verify >1 interval sums, not first-only.
            const string json = """
                {
                  "code": 0,
                  "data": {
                    "performance": {
                      "intervals": [
                        { "traffic": { "comments": 0, "likes": 13, "new_followers": 0, "shares": 0, "views": 263 } },
                        { "traffic": { "comments": 2, "likes": 4, "new_followers": 1, "shares": 3, "views": 37 } }
                      ]
                    }
                  }
                }
                """;

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;
            var dm = svc.ParseDetailsMetrics(data);

            Assert.Equal(300, dm.Views);      // 263 + 37
            Assert.Equal(17, dm.Likes);       // 13 + 4
            Assert.Equal(2, dm.Comments);
            Assert.Equal(3, dm.Shares);
            Assert.Equal(1, dm.NewFollowers);
        }
    }
}
