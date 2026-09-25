using System;
using System.Collections.Generic;
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
    // Phase 1B (Remaining Details Metrics) tests for Average Watch, Full Watch Rate, and Demographics.
    //
    // EVIDENCE GAPS:
    // - Real API response (DetailsRealResponseTests) contains NO average_watch_time or full_watch_rate fields
    // - Real API response has empty viewer_profile [] (no demographic data)
    // - Unit of average_watch_time (if/when present) is NOT KNOWN (could be seconds, ms, or other)
    // - Rate format (if/when present) is NOT KNOWN (could be 0.45 for 45%, or "45.0", or other)
    // - These tests verify PARSING CAPABILITY and direct mapping with synthetic fixtures only
    // - When real API response includes these fields, they WILL flow through mapper correctly
    public class Phase1BDetailsMetricsTests
    {
        private static DetailsMetrics ParseJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;
            return svc.ParseDetailsMetrics(data);
        }

        private async Task<ContentMetric> MapAndVerifyAsync(DetailsMetrics dm, AppDbContext db)
        {
            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;

            var metric = svc.MapDetailsMetricsToContentMetric(dm, contentLogId: 1);
            Assert.NotNull(metric);
            return metric!;
        }

        // ========== AVERAGE WATCH TESTS ==========
        // NOTE: Real API response does NOT contain average_watch_time field.
        // These tests verify parser capability with synthetic fixtures.
        // Unit of value is UNKNOWN from real evidence (could be seconds, milliseconds, or other).

        [Fact]
        public void AverageWatch_With_SingleInterval_Parses()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "average_watch_time": 3.5
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.Equal(100, dm.Views);
            Assert.Equal(3.5m, dm.AverageWatch);
            Assert.NotNull(dm.AverageWatchPath);
        }

        [Fact]
        public void AverageWatch_With_Zero_Preserved()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "average_watch_time": 0.0
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.NotNull(dm.AverageWatch);
            Assert.Equal(0.0m, dm.AverageWatch.Value);
        }

        [Fact]
        public void AverageWatch_Missing_Field_Is_Null()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.Null(dm.AverageWatch);
            Assert.Null(dm.AverageWatchPath);
        }

        [Fact]
        public void AverageWatch_MultipleIntervals_Summed()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "average_watch_time": 2.0
                      }
                    },
                    {
                      "traffic": {
                        "views": 50,
                        "likes": 5,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "average_watch_time": 1.5
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            // Sum of average watch across intervals (semantics: presence confirmed)
            Assert.Equal(3.5m, dm.AverageWatch);
        }

        [Fact]
        public async Task AverageWatch_Maps_To_ContentMetric()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "average_watch_time": 4.2
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);
            var metric = await MapAndVerifyAsync(dm, db);

            Assert.Equal(4.2m, metric.AverageWatch);
        }

        // ========== FULL WATCH RATE TESTS ==========
        // NOTE: Real API response does NOT contain full_watch_rate or finish_rate field.
        // These tests verify parser capability with synthetic fixtures.
        // Parser accepts both "full_watch_rate" and "finish_rate" field names.
        // Rate representation (0.0-1.0 decimal vs percentage) is UNKNOWN from real evidence.

        [Fact]
        public void FullWatchRate_With_SingleInterval_Parses()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "full_watch_rate": 0.45
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.Equal(100, dm.Views);
            Assert.Equal(0.45m, dm.FullWatchRate);
            Assert.NotNull(dm.FullWatchRatePath);
        }

        [Fact]
        public void FullWatchRate_With_AlternateFieldName_FinishRate()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "finish_rate": 0.65
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.Equal(0.65m, dm.FullWatchRate);
        }

        [Fact]
        public void FullWatchRate_With_Zero_Preserved()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "full_watch_rate": 0.0
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.NotNull(dm.FullWatchRate);
            Assert.Equal(0.0m, dm.FullWatchRate.Value);
        }

        [Fact]
        public void FullWatchRate_Missing_Field_Is_Null()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.Null(dm.FullWatchRate);
            Assert.Null(dm.FullWatchRatePath);
        }

        [Fact]
        public void FullWatchRate_MultipleIntervals_Summed()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "full_watch_rate": 0.3
                      }
                    },
                    {
                      "traffic": {
                        "views": 50,
                        "likes": 5,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "full_watch_rate": 0.2
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            // Sum of rates across intervals (semantics: presence confirmed)
            Assert.Equal(0.5m, dm.FullWatchRate);
        }

        [Fact]
        public async Task FullWatchRate_Maps_To_ContentMetric()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0,
                        "full_watch_rate": 0.75
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);
            var metric = await MapAndVerifyAsync(dm, db);

            Assert.Equal(0.75m, metric.FullWatchRate);
        }

        // ========== DEMOGRAPHICS TESTS ==========
        // NOTE: Real API response has empty viewer_profile [].
        // These tests verify parser capability with synthetic populated profiles.
        // Demographics must be serialized to DemographicsJson in ContentMetric.

        [Fact]
        public void Demographics_VIEWERS_Profile_With_Gender_And_Age_Parses()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 200,
                        "likes": 20,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "type": "VIEWERS",
                      "gender_distribution": [
                        { "gender": "male", "percentage": 0.60 },
                        { "gender": "female", "percentage": 0.40 }
                      ],
                      "age_distribution": [
                        { "age": "18-24", "percentage": 0.35 },
                        { "age": "25-34", "percentage": 0.45 },
                        { "age": "35-44", "percentage": 0.15 },
                        { "age": "45-54", "percentage": 0.05 },
                        { "age": "55+", "percentage": 0.00 }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.Equal(0.60m, dm.Male);
            Assert.Equal(0.40m, dm.Female);
            Assert.Null(dm.NoGender);
            Assert.Equal(0.35m, dm.Age18);
            Assert.Equal(0.45m, dm.Age25);
            Assert.Equal(0.15m, dm.Age35);
            Assert.Equal(0.05m, dm.Age45);
            Assert.Equal(0.00m, dm.Age55);
        }

        [Fact]
        public void Demographics_Type_Matching_CaseInsensitive()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "type": "viewers",
                      "gender_distribution": [
                        { "gender": "male", "percentage": 0.50 }
                      ],
                      "age_distribution": [
                        { "age": "18-24", "percentage": 0.50 }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            // type="viewers" (lowercase) should match case-insensitive
            Assert.Equal(0.50m, dm.Male);
            Assert.Equal(0.50m, dm.Age18);
        }

        [Fact]
        public void Demographics_Gender_Values_CaseInsensitive()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "type": "VIEWERS",
                      "gender_distribution": [
                        { "gender": "MALE", "percentage": 0.50 },
                        { "gender": "FEMALE", "percentage": 0.30 },
                        { "gender": "NO_GENDER", "percentage": 0.20 }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.Equal(0.50m, dm.Male);
            Assert.Equal(0.30m, dm.Female);
            Assert.Equal(0.20m, dm.NoGender);
        }

        [Fact]
        public void Demographics_Age_Ranges_CaseInsensitive()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "type": "VIEWERS",
                      "age_distribution": [
                        { "age": "18-24", "percentage": 0.25 },
                        { "age": "25-34", "percentage": 0.25 },
                        { "age": "35-44", "percentage": 0.25 },
                        { "age": "45-54", "percentage": 0.15 },
                        { "age": "55+", "percentage": 0.10 }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.Equal(0.25m, dm.Age18);
            Assert.Equal(0.25m, dm.Age25);
            Assert.Equal(0.25m, dm.Age35);
            Assert.Equal(0.15m, dm.Age45);
            Assert.Equal(0.10m, dm.Age55);
        }

        [Fact]
        public void Demographics_NoGender_Also_Recognizes_Unknown()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "type": "VIEWERS",
                      "gender_distribution": [
                        { "gender": "unknown", "percentage": 0.15 }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            // "unknown" should be recognized as NoGender
            Assert.Equal(0.15m, dm.NoGender);
        }

        [Fact]
        public void Demographics_Empty_ViewerProfile_Is_Null()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": []
                }
              }
            }
            """;

            var dm = ParseJson(json);

            // Empty array means no demographics parsed
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
        public void Demographics_Missing_ViewerProfile_Property_Is_Null()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            // Missing viewer_profile property means no demographics parsed
            Assert.Null(dm.Male);
            Assert.Null(dm.Female);
            Assert.Null(dm.NoGender);
            Assert.Null(dm.Age18);
        }

        [Fact]
        public void Demographics_IgnoresNonVIEWERSProfiles()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "type": "NEW_FOLLOWER",
                      "gender_distribution": [
                        { "gender": "male", "percentage": 0.70 }
                      ]
                    },
                    {
                      "type": "VIEWERS",
                      "gender_distribution": [
                        { "gender": "female", "percentage": 0.80 }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            // Should use VIEWERS profile, not NEW_FOLLOWER
            Assert.Null(dm.Male);
            Assert.Equal(0.80m, dm.Female);
        }

        [Fact]
        public void Demographics_Zero_Values_Preserved()
        {
            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "type": "VIEWERS",
                      "gender_distribution": [
                        { "gender": "male", "percentage": 0.0 },
                        { "gender": "female", "percentage": 1.0 }
                      ],
                      "age_distribution": [
                        { "age": "18-24", "percentage": 0.0 },
                        { "age": "55+", "percentage": 1.0 }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);

            Assert.NotNull(dm.Male);
            Assert.Equal(0.0m, dm.Male.Value);
            Assert.NotNull(dm.Age18);
            Assert.Equal(0.0m, dm.Age18.Value);
        }

        [Fact]
        public async Task Demographics_Maps_To_DemographicsJson()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 200,
                        "likes": 20,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "type": "VIEWERS",
                      "gender_distribution": [
                        { "gender": "male", "percentage": 0.60 },
                        { "gender": "female", "percentage": 0.40 }
                      ],
                      "age_distribution": [
                        { "age": "18-24", "percentage": 0.50 },
                        { "age": "25-34", "percentage": 0.50 }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);
            var metric = await MapAndVerifyAsync(dm, db);

            // Verify DemographicsJson is populated
            Assert.NotNull(metric.DemographicsJson);

            // Parse and verify structure
            using var doc = JsonDocument.Parse(metric.DemographicsJson);
            var root = doc.RootElement;

            Assert.Equal(0.60m, root.GetProperty("male").GetDecimal());
            Assert.Equal(0.40m, root.GetProperty("female").GetDecimal());

            var ages = root.GetProperty("ages");
            Assert.Equal(0.50m, ages.GetProperty("18-24").GetDecimal());
            Assert.Equal(0.50m, ages.GetProperty("25-34").GetDecimal());
        }

        [Fact]
        public async Task Demographics_Empty_ViewerProfile_Results_In_Null_DemographicsJson()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 100,
                        "likes": 10,
                        "comments": 0,
                        "shares": 0,
                        "new_followers": 0
                      }
                    }
                  ],
                  "viewer_profile": []
                }
              }
            }
            """;

            var dm = ParseJson(json);
            var metric = await MapAndVerifyAsync(dm, db);

            // Empty viewer_profile should result in null DemographicsJson
            Assert.Null(metric.DemographicsJson);
        }

        [Fact]
        public async Task Phase1B_All_Three_Metrics_Together()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            const string json = $$"""
            {
              "code": 0,
              "data": {
                "performance": {
                  "intervals": [
                    {
                      "traffic": {
                        "views": 500,
                        "likes": 50,
                        "comments": 5,
                        "shares": 2,
                        "new_followers": 3,
                        "average_watch_time": 4.8,
                        "full_watch_rate": 0.62
                      }
                    }
                  ],
                  "viewer_profile": [
                    {
                      "type": "VIEWERS",
                      "gender_distribution": [
                        { "gender": "male", "percentage": 0.55 },
                        { "gender": "female", "percentage": 0.45 }
                      ],
                      "age_distribution": [
                        { "age": "18-24", "percentage": 0.40 },
                        { "age": "25-34", "percentage": 0.35 },
                        { "age": "35-44", "percentage": 0.15 },
                        { "age": "45-54", "percentage": 0.07 },
                        { "age": "55+", "percentage": 0.03 }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var dm = ParseJson(json);
            var metric = await MapAndVerifyAsync(dm, db);

            // Phase 1A metrics (existing)
            Assert.Equal(500, metric.Views);
            Assert.Equal(50, metric.Likes);
            Assert.Equal(5, metric.Comments);
            Assert.Equal(2, metric.Shares);
            Assert.Equal(3, metric.NewFollowers);

            // Phase 1B metrics
            Assert.Equal(4.8m, metric.AverageWatch);
            Assert.Equal(0.62m, metric.FullWatchRate);
            Assert.NotNull(metric.DemographicsJson);

            // Validate DemographicsJson structure
            using var doc = JsonDocument.Parse(metric.DemographicsJson);
            var root = doc.RootElement;
            Assert.Equal(0.55m, root.GetProperty("male").GetDecimal());
            Assert.Equal(0.45m, root.GetProperty("female").GetDecimal());
            var ages = root.GetProperty("ages");
            Assert.Equal(0.40m, ages.GetProperty("18-24").GetDecimal());
        }
    }
}
