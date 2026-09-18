using System;
using System.Text.Json;
using System.Runtime.Serialization;
using Xunit;
using KaleContentOps.Services.TikTok;

namespace KaleContentOps.TikTok.Tests
{
    public class DetailsParserTrafficAbsenceTests
    {
        [Fact]
        public void ParseDetailsMetrics_TrafficExists_AllZero_ResultsInZeroValues()
        {
            var sample = "{\"code\":0,\"data\":{\"performance\":{\"intervals\":[{\"traffic\":{\"views\":0,\"likes\":0,\"comments\":0,\"shares\":0,\"new_followers\":0}}]}}}";

            using var doc = JsonDocument.Parse(sample);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;

            var dm = svc.ParseDetailsMetrics(data);

            Assert.True(dm.Views.HasValue);
            Assert.Equal(0, dm.Views);
            Assert.True(dm.Likes.HasValue);
            Assert.Equal(0, dm.Likes);
            Assert.True(dm.Comments.HasValue);
            Assert.Equal(0, dm.Comments);
            Assert.True(dm.Shares.HasValue);
            Assert.Equal(0, dm.Shares);
            Assert.True(dm.NewFollowers.HasValue);
            Assert.Equal(0, dm.NewFollowers);
        }

        [Fact]
        public void ParseDetailsMetrics_IntervalsExist_NoTraffic_FieldsAreNull()
        {
            var sample = "{\"code\":0,\"data\":{\"performance\":{\"intervals\":[{\"start_date\":\"2026-08-12\",\"end_date\":\"2026-08-13\",\"sales\":{\"breakdowns\":[]}}]}}}";

            using var doc = JsonDocument.Parse(sample);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;

            var dm = svc.ParseDetailsMetrics(data);

            Assert.Null(dm.Views);
            Assert.Null(dm.Likes);
            Assert.Null(dm.Comments);
            Assert.Null(dm.Shares);
            Assert.Null(dm.NewFollowers);
        }
    }
}
