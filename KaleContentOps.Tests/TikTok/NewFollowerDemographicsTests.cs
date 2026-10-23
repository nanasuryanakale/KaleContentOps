using System;
using System.Runtime.Serialization;
using System.Text.Json;
using KaleContentOps.Services.TikTok;
using Xunit;

namespace KaleContentOps.TikTok.Tests
{
    // Focused tests for viewer_profile profile-type handling.
    // Fixtures are sanitized minimal shapes based on actually captured 202509 details
    // responses (no tokens, request ids, or account data).
    //
    // Semantics under test:
    //   VIEWERS      -> existing Content Log audience fields (Male/Female/NoGender/ages/countries)
    //   NEW_FOLLOWER -> separate population (Nf* fields); never substituted into VIEWERS fields.
    public class NewFollowerDemographicsTests
    {
        private static DetailsMetrics Parse(string wrappedJson)
        {
            using var doc = JsonDocument.Parse(wrappedJson);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

            var svc = (TikTokVideoService)FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            return svc.ParseDetailsMetrics(data);
        }

        // ------------------------------------------------------------------
        // Case 2: NEW_FOLLOWER-only response (real captured shape: male=0.781,
        // female=0.219; age buckets on the same profile). Must be captured into
        // the Nf* fields, and must NOT populate the VIEWERS audience fields.
        // ------------------------------------------------------------------
        [Fact]
        public void NewFollowerOnly_Profile_Captured_And_Not_Substituted_Into_Viewers()
        {
            var dm = Parse("""
                {
                  "data": {
                    "performance": {
                      "intervals": [],
                      "viewer_profile": [
                        {
                          "type": "NEW_FOLLOWER",
                          "gender_distribution": [
                            { "gender": "male", "percentage": "0.781" },
                            { "gender": "female", "percentage": "0.219" }
                          ],
                          "age_distribution": [
                            { "age": "25-34", "percentage": "0.500" },
                            { "age": "35-44", "percentage": "0.219" },
                            { "age": "18-24", "percentage": "0.250" },
                            { "age": "55 and above", "percentage": "0.031" }
                          ],
                          "country_distribution": [
                            { "country_code": "ID", "percentage": "0.969" },
                            { "country_code": "AU", "percentage": "0.031" }
                          ]
                        }
                      ]
                    }
                  }
                }
                """);

            // Captured
            Assert.Equal(0.781m, dm.NfMale);
            Assert.Equal(0.219m, dm.NfFemale);
            Assert.Equal(0.500m, dm.NfAge25);
            Assert.Equal(0.031m, dm.NfAge55);
            Assert.NotNull(dm.NfCountries);
            Assert.Equal(0.969m, dm.NfCountries!["ID"]);

            // NOT substituted into VIEWERS fields
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

        // ------------------------------------------------------------------
        // Case 1: Existing VIEWERS-only response keeps existing behavior
        // (verified persisted example: male=0.700, female=0.300) and must not
        // produce NEW_FOLLOWER values.
        // ------------------------------------------------------------------
        [Fact]
        public void ViewersOnly_Profile_Keeps_Existing_Behavior()
        {
            var dm = Parse("""
                {
                  "data": {
                    "performance": {
                      "intervals": [],
                      "viewer_profile": [
                        {
                          "type": "VIEWERS",
                          "gender_distribution": [
                            { "gender": "male", "percentage": "0.700" },
                            { "gender": "female", "percentage": "0.300" }
                          ],
                          "age_distribution": [
                            { "age": "18-24", "percentage": "0.222" },
                            { "age": "25-34", "percentage": "0.444" },
                            { "age": "35-44", "percentage": "0.333" }
                          ]
                        }
                      ]
                    }
                  }
                }
                """);

            Assert.Equal(0.700m, dm.Male);
            Assert.Equal(0.300m, dm.Female);
            Assert.Equal(0.222m, dm.Age18);
            Assert.Equal(0.444m, dm.Age25);
            Assert.Equal(0.333m, dm.Age35);

            Assert.Null(dm.NfMale);
            Assert.Null(dm.NfFemale);
            Assert.Null(dm.NfNoGender);
            Assert.Null(dm.NfCountries);
        }

        // ------------------------------------------------------------------
        // Case 3: both profiles in one response, in BOTH array orders.
        // Each profile must land in its own scope; ordering must not matter.
        // ------------------------------------------------------------------
        [Fact]
        public void Both_Profiles_Persisted_Separately_Order_Independent()
        {
            const string nfFirst = """
                {
                  "data": {
                    "performance": {
                      "viewer_profile": [
                        { "type": "NEW_FOLLOWER",
                          "gender_distribution": [ { "gender": "male", "percentage": "0.781" }, { "gender": "female", "percentage": "0.219" } ] },
                        { "type": "VIEWERS",
                          "gender_distribution": [ { "gender": "male", "percentage": "0.700" }, { "gender": "female", "percentage": "0.300" } ] }
                      ]
                    }
                  }
                }
                """;

            const string viewersFirst = """
                {
                  "data": {
                    "performance": {
                      "viewer_profile": [
                        { "type": "VIEWERS",
                          "gender_distribution": [ { "gender": "male", "percentage": "0.700" }, { "gender": "female", "percentage": "0.300" } ] },
                        { "type": "NEW_FOLLOWER",
                          "gender_distribution": [ { "gender": "male", "percentage": "0.781" }, { "gender": "female", "percentage": "0.219" } ] }
                      ]
                    }
                  }
                }
                """;

            foreach (var json in new[] { nfFirst, viewersFirst })
            {
                var dm = Parse(json);

                Assert.Equal(0.700m, dm.Male);
                Assert.Equal(0.300m, dm.Female);
                Assert.Equal(0.781m, dm.NfMale);
                Assert.Equal(0.219m, dm.NfFemale);
            }
        }

        // ------------------------------------------------------------------
        // Case 4: VIEWERS absent or empty; NEW_FOLLOWER present.
        // VIEWERS fields stay null - no substitution in either direction.
        // ------------------------------------------------------------------
        [Fact]
        public void Viewers_Absent_Or_Empty_Nulls_Preserved()
        {
            // viewer_profile missing entirely
            var dmAbsent = Parse("""
                { "data": { "performance": { "intervals": [] } } }
                """);
            Assert.Null(dmAbsent.Male);
            Assert.Null(dmAbsent.NfMale);

            // viewer_profile present but empty array
            var dmEmpty = Parse("""
                { "data": { "performance": { "viewer_profile": [] } } }
                """);
            Assert.Null(dmEmpty.Male);
            Assert.Null(dmEmpty.NfMale);

            // NEW_FOLLOWER present, VIEWERS absent
            var dmNfOnly = Parse("""
                {
                  "data": { "performance": { "viewer_profile": [
                    { "type": "NEW_FOLLOWER",
                      "gender_distribution": [ { "gender": "male", "percentage": "0.781" } ] }
                  ] } }
                }
                """);
            Assert.Equal(0.781m, dmNfOnly.NfMale);
            Assert.Null(dmNfOnly.Male); // must NOT be substituted
        }

        // ------------------------------------------------------------------
        // Case 5: missing / empty / partial distributions stay null or empty -
        // no invented zeros. Mirrors the real payload where one profile has
        // empty age/country arrays and only gender has values.
        // ------------------------------------------------------------------
        [Fact]
        public void Partial_And_Empty_Distributions_Do_Not_Invent_Values()
        {
            var dm = Parse("""
                {
                  "data": { "performance": { "viewer_profile": [
                    {
                      "type": "NEW_FOLLOWER",
                      "gender_distribution": [ { "gender": "male", "percentage": "0.781" }, { "gender": "female", "percentage": "0.219" } ],
                      "age_distribution": [],
                      "country_distribution": []
                    }
                  ] } }
                }
                """);

            Assert.Equal(0.781m, dm.NfMale);
            Assert.Equal(0.219m, dm.NfFemale);
            Assert.Null(dm.NfAge18);   // empty array -> no buckets invented
            Assert.Null(dm.NfAge25);
            Assert.Null(dm.NfAge55);
            Assert.Null(dm.NfNoGender);
            Assert.Null(dm.NfCountries);

            // unknown/blank entries are skipped without inventing data
            var dmSkips = Parse("""
                {
                  "data": { "performance": { "viewer_profile": [
                    { "type": "VIEWERS",
                      "gender_distribution": [ { "gender": "", "percentage": "0.5" }, { "gender": "male", "percentage": "0.700" } ],
                      "country_distribution": [ { "country_code": "  ", "percentage": "0.5" }, { "country_code": "ID", "percentage": "1.000" } ] }
                  ] } }
                }
                """);
            Assert.Equal(0.700m, dmSkips.Male);
            Assert.Null(dmSkips.NoGender);
            Assert.NotNull(dmSkips.Countries);
            Assert.Single(dmSkips.Countries!);
            Assert.Equal(1.000m, dmSkips.Countries!["ID"]);
        }

        // ------------------------------------------------------------------
        // Mapping: NEW_FOLLOWER-only parse -> NewFollowerDemographicsJson
        // persisted; DemographicsJson (VIEWERS) stays null; then both-profiles
        // parse -> both JSON columns populated, never merged.
        // ------------------------------------------------------------------
        [Fact]
        public void MapDetailsMetrics_Persists_Profiles_In_Separate_Columns()
        {
            var svc = (TikTokVideoService)FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));

            // NEW_FOLLOWER-only
            var nfOnly = Parse("""
                {
                  "data": { "performance": { "viewer_profile": [
                    { "type": "NEW_FOLLOWER",
                      "gender_distribution": [ { "gender": "male", "percentage": "0.781" }, { "gender": "female", "percentage": "0.219" } ] }
                  ] } }
                }
                """);
            var metricNf = svc.MapDetailsMetricsToContentMetric(nfOnly, contentLogId: 1);
            Assert.NotNull(metricNf);
            Assert.Null(metricNf!.DemographicsJson);
            Assert.NotNull(metricNf.NewFollowerDemographicsJson);
            using (var doc = JsonDocument.Parse(metricNf.NewFollowerDemographicsJson!))
            {
                // decimal values serialize as JSON numbers (not strings)
                Assert.Equal(0.781m, doc.RootElement.GetProperty("male").GetDecimal());
                Assert.Equal(0.219m, doc.RootElement.GetProperty("female").GetDecimal());
            }

            // Both profiles
            var both = Parse("""
                {
                  "data": { "performance": { "viewer_profile": [
                    { "type": "VIEWERS",
                      "gender_distribution": [ { "gender": "male", "percentage": "0.700" }, { "gender": "female", "percentage": "0.300" } ] },
                    { "type": "NEW_FOLLOWER",
                      "gender_distribution": [ { "gender": "male", "percentage": "0.781" }, { "gender": "female", "percentage": "0.219" } ] }
                  ] } }
                }
                """);
            var metricBoth = svc.MapDetailsMetricsToContentMetric(both, contentLogId: 1);
            Assert.NotNull(metricBoth);
            Assert.NotNull(metricBoth!.DemographicsJson);
            Assert.NotNull(metricBoth.NewFollowerDemographicsJson);
            using (var v = JsonDocument.Parse(metricBoth.DemographicsJson!))
            {
                Assert.Equal(0.700m, v.RootElement.GetProperty("male").GetDecimal());
                Assert.Equal(0.300m, v.RootElement.GetProperty("female").GetDecimal());
            }
            using (var n = JsonDocument.Parse(metricBoth.NewFollowerDemographicsJson!))
            {
                Assert.Equal(0.781m, n.RootElement.GetProperty("male").GetDecimal());
                Assert.Equal(0.219m, n.RootElement.GetProperty("female").GetDecimal());
            }
        }

        // ------------------------------------------------------------------
        // Mapping: response with no demographics at all -> both columns null
        // (no fabricated objects, no {} payloads).
        // ------------------------------------------------------------------
        [Fact]
        public void NoDemographics_Both_Json_Columns_Null()
        {
            var dm = Parse("""
                { "data": { "performance": { "intervals": [] } } }
                """);

            var svc = (TikTokVideoService)FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var metric = svc.MapDetailsMetricsToContentMetric(dm, contentLogId: 1);

            Assert.NotNull(metric);
            Assert.Null(metric!.DemographicsJson);
            Assert.Null(metric.NewFollowerDemographicsJson);
        }
    }
}
