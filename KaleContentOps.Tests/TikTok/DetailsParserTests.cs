using System;
using System.Text.Json;
using System.Runtime.Serialization;
using Xunit;
using KaleContentOps.Services.TikTok;

namespace KaleContentOps.TikTok.Tests
{
    public class DetailsParserTests
    {
        [Fact]
        public void ParseDetailsMetrics_Should_Parse_Demographics_From_Sample()
        {
            var sample = "{\"code\":0,\"data\":{\"performance\":{\"intervals\":[{\"traffic\":{\"views\":23,\"likes\":0,\"comments\":0,\"shares\":0,\"new_followers\":0}}],\"viewer_profile\":[{\"type\":\"VIEWERS\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.500\"},{\"gender\":\"female\",\"percentage\":\"0.500\"}],\"age_distribution\":[{\"age\":\"18-24\",\"percentage\":\"0.500\"},{\"age\":\"25-34\",\"percentage\":\"0.250\"},{\"age\":\"35-44\",\"percentage\":\"0.250\"}]},{\"type\":\"NEW_FOLLOWER\",\"gender_distribution\":[{\"gender\":\"male\",\"percentage\":\"0.600\"}],\"age_distribution\":[{\"age\":\"25-34\",\"percentage\":\"0.600\"}]}]}}}";

            using var doc = JsonDocument.Parse(sample);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

            var svcObj = FormatterServices.GetUninitializedObject(typeof(TikTokVideoService));
            var svc = (TikTokVideoService)svcObj;

            var dm = svc.ParseDetailsMetrics(data);

            Assert.Equal(23, dm.Views);
            Assert.Equal(0, dm.Likes);
            Assert.Equal(0, dm.Comments);
            Assert.Equal(0, dm.Shares);
            Assert.Equal(0, dm.NewFollowers);

            Assert.Equal(0.500m, dm.Male);
            Assert.Equal(0.500m, dm.Female);

            Assert.Equal(0.500m, dm.Age18);
            Assert.Equal(0.250m, dm.Age25);
            Assert.Equal(0.250m, dm.Age35);
            Assert.Null(dm.Age45);
            Assert.Null(dm.Age55);
        }
    }
}
