// Controlled live probe (max 1 TikTok request): does
//   GET /analytics/202609/shop_videos/batch_performance?granularity=1D
// return PER-DAY intervals with traffic.likes/comments/shares?
//
// Prints ONLY non-sensitive facts (status, code, interval dates, field presence).
// Never prints app_secret, access token, signature or the signed URL.
// Raw response body (contains no credentials) is saved to .tmp/batch-granularity-probe.json.

using System.Globalization;
using System.Text.Json;
using KaleContentOps.Data;
using KaleContentOps.Services;
using KaleContentOps.Services.TikTok;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var appDir = Path.Combine(repoRoot, "KaleContentOps");

var config = new ConfigurationBuilder()
    .SetBasePath(appDir)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddUserSecrets(typeof(Program).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLogging();
var connection = config.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection missing.");
services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connection));
services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(appDir, "App_Data", "DataProtection-Keys")))
    .SetApplicationName("KaleContentOps");
services.AddHttpClient("TikTokApi", c =>
{
    c.BaseAddress = new Uri(config["TikTok:BaseUrl"] ?? "https://open-api.tiktokglobalshop.com");
    c.Timeout = TimeSpan.FromSeconds(30);
});
services.AddHttpClient("TikTokAuth", c =>
{
    c.BaseAddress = new Uri(config["TikTok:AuthBaseUrl"] ?? "https://auth.tiktok-shops.com");
    c.Timeout = TimeSpan.FromSeconds(30);
});
services.Configure<TikTokOptions>(config.GetSection("TikTok"));
services.AddSingleton<ITikTokSignatureService, TikTokSignatureService>();
services.AddScoped<ITikTokAuthService, TikTokAuthService>();

await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
var options = scope.ServiceProvider.GetRequiredService<IOptions<TikTokOptions>>().Value;
var authService = scope.ServiceProvider.GetRequiredService<ITikTokAuthService>();
var signature = scope.ServiceProvider.GetRequiredService<ITikTokSignatureService>();
var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("TikTokApi");

if (string.IsNullOrWhiteSpace(options.AppKey) || string.IsNullOrWhiteSpace(options.AppSecret))
{
    Console.WriteLine("FATAL: TikTok:AppKey/AppSecret not configured.");
    return 1;
}

var shopCipher = options.DefaultShopCipher
    ?? await db.TikTokShops.AsNoTracking()
        .Where(s => s.ShopCipher != null && s.ShopCipher != "")
        .OrderBy(s => s.Id)
        .Select(s => s.ShopCipher)
        .FirstOrDefaultAsync();
if (string.IsNullOrWhiteSpace(shopCipher))
{
    Console.WriteLine("FATAL: no shop cipher.");
    return 1;
}

var credential = await db.TikTokCredentials.AsNoTracking()
    .FirstOrDefaultAsync(x => x.AppKey == options.AppKey);
if (credential is null)
{
    Console.WriteLine("FATAL: no credential row for the configured AppKey.");
    return 1;
}

var token = await authService.GetValidAccessTokenAsync(credential.Id);
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("FATAL: access token unavailable.");
    return 1;
}

var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta")).Date; // fallback-safe below
try
{
    var shopTz = new ShopTimeZone(Options.Create(new ShopTimeZoneOptions()));
    today = shopTz.TodayMidnight();
}
catch
{
    // keep UtcNow-based fallback
}
var endExclusive = today;               // exclusive end = today
var windowDays = args.Length > 0 && int.TryParse(args[0], out var wd) ? wd : 8;
var endOffsetDays = args.Length > 1 && int.TryParse(args[1], out var eo) ? eo : 0;
var start = endExclusive.AddDays(-windowDays - endOffsetDays);
endExclusive = endExclusive.AddDays(-endOffsetDays);
// 3 OLDER videos (posted 7-20 days ago) so a multi-day daily split is observable.
var videoIds = await db.ContentLogs.AsNoTracking()
    .Where(x => x.VideoId != null && x.VideoId != "" && x.VideoPostTime != null
        && x.VideoPostTime < today.AddDays(-7) && x.VideoPostTime >= today.AddDays(-25))
    .OrderByDescending(x => x.VideoPostTime)
    .Take(3)
    .Select(x => x.VideoId)
    .ToListAsync();
if (videoIds.Count == 0)
{
    Console.WriteLine("FATAL: no ContentLog videos with post time.");
    return 1;
}

// Window: last 8 complete Jakarta days (half-open, same convention as the app).

var path = "/analytics/202609/shop_videos/batch_performance";
var query = new Dictionary<string, string?>
{
    { "app_key", options.AppKey },
    { "shop_cipher", shopCipher },
    { "start_date_ge", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
    { "end_date_lt", endExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
    { "granularity", "1D" },
    { "video_ids", string.Join(",", videoIds) },
    { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }
};

var signInput = query.Where(kv => !string.IsNullOrEmpty(kv.Value))
    .ToDictionary(kv => kv.Key, kv => kv.Value);
var sign = signature.GenerateSignature("GET", path, signInput, null, null);
signInput.Add("sign", sign);
var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(path, signInput);

Console.WriteLine($"PROBE request: {path} granularity=1D window={start:yyyy-MM-dd}..{endExclusive:yyyy-MM-dd} videos={videoIds.Count} ({string.Join(", ", videoIds)})");

using var req = new HttpRequestMessage(HttpMethod.Get, url);
req.Headers.Add("x-tts-access-token", token);
req.Headers.Add("Accept", "application/json");
using var res = await http.SendAsync(req);
var body = await res.Content.ReadAsStringAsync();

Console.WriteLine($"HTTP {(int)res.StatusCode}");
if ((int)res.StatusCode != 200)
{
    // Error body (no credentials) - print it verbatim for diagnosis.
    Console.WriteLine("BODY: " + body.Substring(0, Math.Min(body.Length, 600)));
    return 0;
}

try
{
    File.WriteAllText(Path.Combine(repoRoot, ".tmp", "batch-granularity-probe.json"), body);
}
catch
{
    // saving is best-effort diagnostics
}

using var doc = JsonDocument.Parse(body);
var root = doc.RootElement;
var code = root.TryGetProperty("code", out var c) ? c.ToString() : "-";
var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
var requestId = root.TryGetProperty("request_id", out var r) ? r.GetString() : null;
Console.WriteLine($"code={code} message={message} request_id={requestId}");

if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
{
    Console.WriteLine("data keys: " + string.Join(", ", data.EnumerateObject().Select(p => p.Name)));
    if (data.TryGetProperty("latest_available_date", out var lad))
        Console.WriteLine($"latest_available_date={lad.GetString()}");

    if (data.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Array)
    {
        Console.WriteLine($"videos returned: {videos.GetArrayLength()}");
        foreach (var v in videos.EnumerateArray())
        {
            var id = v.TryGetProperty("video_id", out var vid) ? vid.GetString() : "?";
            if (!v.TryGetProperty("performance", out var perf) || perf.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine($"  video {id}: NO performance object");
                continue;
            }
            if (!perf.TryGetProperty("intervals", out var iv) || iv.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine($"  video {id}: NO intervals array");
                continue;
            }
            Console.WriteLine($"  video {id}: intervals={iv.GetArrayLength()}");
            foreach (var i in iv.EnumerateArray())
            {
                var s = i.TryGetProperty("start_date", out var sd) ? sd.GetString() : "?";
                var e = i.TryGetProperty("end_date", out var ed) ? ed.GetString() : "?";
                var traffic = i.TryGetProperty("traffic", out var t) && t.ValueKind == JsonValueKind.Object
                    ? string.Join(",", t.EnumerateObject().Select(p => p.Name))
                    : "(no traffic object)";
                Console.WriteLine($"    {s}..{e}: traffic[{traffic}]");
            }
        }
    }
    else
    {
        Console.WriteLine("data.videos missing or not an array - schema mismatch or error.");
    }
}
else
{
    Console.WriteLine("no data object.");
}

return 0;
