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

        // ========== REAL VIEWER_PROFILE REGRESSION (VideoId 7687996887235890452, captured 2026-09-29) ==========
        // Real payload carries viewer_profile[type=NEW_FOLLOWER] FIRST and viewer_profile[type=VIEWERS] SECOND.
        // ContentLog demographics MUST come from the VIEWERS profile only.
        private const string RealViewerProfileJson = """
            {
              "code": 0,
              "message": "Success",
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "comments": 0,
                        "likes": 15,
                        "new_followers": 1,
                        "shares": 0,
                        "views": 477
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "age_distribution": [
                        { "age": "13-17", "percentage": "0.002" },
                        { "age": "18-24", "percentage": "0.137" },
                        { "age": "35-44", "percentage": "0.176" }
                      ],
                      "country_distribution": [
                        { "country_code": "", "percentage": "0.002" },
                        { "country_code": "MY", "percentage": "0.007" },
                        { "country_code": "ID", "percentage": "0.990" }
                      ],
                      "gender_distribution": [
                        { "gender": "male", "percentage": "0.543" },
                        { "gender": "female", "percentage": "0.457" }
                      ],
                      "type": "NEW_FOLLOWER"
                    },
                    {
                      "age_distribution": [
                        { "age": "35-44", "percentage": "0.167" },
                        { "age": "25-34", "percentage": "0.500" },
                        { "age": "45-54", "percentage": "0.056" },
                        { "age": "18-24", "percentage": "0.278" }
                      ],
                      "country_distribution": [
                        { "country_code": "ID", "percentage": "1.000" }
                      ],
                      "gender_distribution": [
                        { "gender": "male", "percentage": "0.684" },
                        { "gender": "female", "percentage": "0.316" }
                      ],
                      "type": "VIEWERS"
                    }
                  ]
                }
              }
            }
            """;

        [Fact]
        public void RealViewerProfile_SelectsVIEWERS_WithRealGenderAgeCountry()
        {
            using var doc = JsonDocument.Parse(RealViewerProfileJson);
            var data = doc.RootElement.GetProperty("data");

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;
            var dm = svc.ParseDetailsMetrics(data);

            // Gender must come from VIEWERS (0.684/0.316), NOT from NEW_FOLLOWER (0.543/0.457)
            Assert.Equal(0.684m, dm.Male);
            Assert.Equal(0.316m, dm.Female);

            // Age must come from VIEWERS only
            Assert.Equal(0.278m, dm.Age18);
            Assert.Equal(0.500m, dm.Age25);
            Assert.Equal(0.167m, dm.Age35);
            Assert.Equal(0.056m, dm.Age45);
            Assert.Null(dm.Age55);

            // Country must come from VIEWERS only (ID = 1.000; NEW_FOLLOWER's blank/MY/ID entries ignored)
            Assert.NotNull(dm.Countries);
            Assert.Single(dm.Countries!);
            Assert.True(dm.Countries!.ContainsKey("ID"));
            Assert.Equal(1.000m, dm.Countries["ID"]);

            // Traffic still parsed from intervals
            Assert.Equal(477, dm.Views);
            Assert.Equal(15, dm.Likes);
        }

        [Fact]
        public void RealViewerProfile_OnlyNewFollower_YieldsNullDemographics()
        {
            // Same payload but with the VIEWERS profile removed: only NEW_FOLLOWER exists.
            const string json = """
                {
                  "code": 0,
                  "data": {
                    "performance": {
                      "intervals": [
                        { "traffic": { "comments": 0, "likes": 15, "new_followers": 1, "shares": 0, "views": 477 } }
                      ],
                      "viewer_profile": [
                        {
                          "age_distribution": [
                            { "age": "18-24", "percentage": "0.137" }
                          ],
                          "country_distribution": [
                            { "country_code": "ID", "percentage": "0.990" }
                          ],
                          "gender_distribution": [
                            { "gender": "male", "percentage": "0.543" },
                            { "gender": "female", "percentage": "0.457" }
                          ],
                          "type": "NEW_FOLLOWER"
                        }
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

            // NEW_FOLLOWER values must NOT leak into demographics
            Assert.Null(dm.Male);
            Assert.Null(dm.Female);
            Assert.Null(dm.NoGender);
            Assert.Null(dm.Age18);
            Assert.Null(dm.Age25);
            Assert.Null(dm.Age35);
            Assert.Null(dm.Age45);
            Assert.Null(dm.Age55);
            Assert.Null(dm.Countries);
        }

        [Fact]
        public async Task RealViewerProfile_Maps_To_DemographicsJson_WithCountries()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);
            var cl = new ContentLog { VideoId = "7687996887235890452" };
            db.ContentLogs.Add(cl);
            await db.SaveChangesAsync();

            using var doc = JsonDocument.Parse(RealViewerProfileJson);
            var data = doc.RootElement.GetProperty("data");

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;

            var dm = svc.ParseDetailsMetrics(data);
            var metric = svc.MapDetailsMetricsToContentMetric(dm, cl.Id);

            Assert.NotNull(metric);
            Assert.Equal(477, metric!.Views);
            Assert.NotNull(metric.DemographicsJson);

            using var demo = JsonDocument.Parse(metric.DemographicsJson!);
            var root = demo.RootElement;

            Assert.Equal(0.684m, root.GetProperty("male").GetDecimal());
            Assert.Equal(0.316m, root.GetProperty("female").GetDecimal());

            var ages = root.GetProperty("ages");
            Assert.Equal(0.278m, ages.GetProperty("18-24").GetDecimal());
            Assert.Equal(0.500m, ages.GetProperty("25-34").GetDecimal());
            Assert.Equal(0.167m, ages.GetProperty("35-44").GetDecimal());
            Assert.Equal(0.056m, ages.GetProperty("45-54").GetDecimal());

            var countries = root.GetProperty("countries");
            Assert.Equal(1.000m, countries.GetProperty("ID").GetDecimal());
        }
    }
}
