using System;
using System.Text.Json;
using KaleContentOps.Services.TikTok;
using Microsoft.Extensions.Options;
using KaleContentOps.Models;

class Program
{
    static int Main(string[] args)
    {
        // Correctly-escaped embedded JSON sample
        var sample = @"{""code"": 0, ""data"": { ""performance"": { ""intervals"": [ { ""traffic"": { ""views"": 23, ""likes"": 0, ""comments"": 0, ""shares"": 0, ""new_followers"": 0 } } ], ""viewer_profile"": [ { ""type"": ""VIEWERS"", ""gender_distribution"": [ { ""gender"": ""male"", ""percentage"": ""0.500"" }, { ""gender"": ""female"", ""percentage"": ""0.500"" } ], ""age_distribution"": [ { ""age"": ""18-24"", ""percentage"": ""0.500"" }, { ""age"": ""25-34"", ""percentage"": ""0.250"" }, { ""age"": ""35-44"", ""percentage"": ""0.250"" } ] }, { ""type"": ""NEW_FOLLOWER"", ""gender_distribution"": [ { ""gender"": ""male"", ""percentage"": ""0.600"" } ], ""age_distribution"": [ { ""age"": ""25-34"", ""percentage"": ""0.600"" } ] } ] } } }";

        using var doc = JsonDocument.Parse(sample);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var dataElem) ? dataElem : root;

        var opts = Options.Create(new TikTokOptions { AppKey = "dummy" });
        // Create an uninitialized instance to call parser methods without satisfying constructor dependencies
        var svcObj = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(KaleContentOps.Services.TikTok.TikTokVideoService));
        var svc = (KaleContentOps.Services.TikTok.TikTokVideoService)svcObj;

        var dm = svc.ParseDetailsMetrics(data);
        var cm = svc.MapDetailsMetricsToContentMetric(dm, contentLogId: 1);

        Console.WriteLine("Parsed DetailsMetrics:");
        Console.WriteLine($"Views: {dm.Views}");
        Console.WriteLine($"Likes: {dm.Likes}");
        Console.WriteLine($"Comments: {dm.Comments}");
        Console.WriteLine($"Shares: {dm.Shares}");
        Console.WriteLine($"NewFollowers: {dm.NewFollowers}");
        var maleStr = dm.Male.HasValue ? dm.Male.Value.ToString("0.000") : "null";
        var femaleStr = dm.Female.HasValue ? dm.Female.Value.ToString("0.000") : "null";
        var age18Str = dm.Age18.HasValue ? dm.Age18.Value.ToString("0.000") : "null";
        var age25Str = dm.Age25.HasValue ? dm.Age25.Value.ToString("0.000") : "null";
        var age35Str = dm.Age35.HasValue ? dm.Age35.Value.ToString("0.000") : "null";
        var age45Str = dm.Age45.HasValue ? dm.Age45.Value.ToString("0.000") : "null";
        var age55Str = dm.Age55.HasValue ? dm.Age55.Value.ToString("0.000") : "null";
        Console.WriteLine("Male: " + maleStr);
        Console.WriteLine("Female: " + femaleStr);
        Console.WriteLine("Age18: " + age18Str);
        Console.WriteLine("Age25: " + age25Str);
        Console.WriteLine("Age35: " + age35Str);
        Console.WriteLine("Age45: " + age45Str);
        Console.WriteLine("Age55: " + age55Str);
        Console.WriteLine($"Reach: {dm.Reach}");
        Console.WriteLine($"AverageWatch: {dm.AverageWatch}");
        Console.WriteLine($"FullWatchRate: {dm.FullWatchRate}");

        Console.WriteLine("\nMapped ContentMetric:");
        Console.WriteLine($"ContentLogId: {cm.ContentLogId}");
        Console.WriteLine($"Views: {cm.Views}");
        Console.WriteLine($"Likes: {cm.Likes}");
        Console.WriteLine($"Comments: {cm.Comments}");
        Console.WriteLine($"Shares: {cm.Shares}");
        Console.WriteLine($"NewFollowers: {cm.NewFollowers}");
        Console.WriteLine($"Reach: {cm.Reach}");
        Console.WriteLine($"AverageWatch: {cm.AverageWatch}");
        Console.WriteLine($"FullWatchRate: {cm.FullWatchRate}");
        Console.WriteLine($"DemographicsJson: {cm.DemographicsJson}");

        return 0;
    }
}
