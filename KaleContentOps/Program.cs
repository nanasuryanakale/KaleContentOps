using KaleContentOps.Data;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Services.TikTok;
using KaleContentOps.Services;
using KaleContentOps.Services.DailySummary;
using System.IO;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Session (used for OAuth state) 
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromMinutes(15);
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
});

// TikTok configuration
builder.Services.Configure<TikTokOptions>(builder.Configuration.GetSection("TikTok"));

// Shop reporting timezone - single shared source for Daily Summary "today" (Issue A)
// and TikTok API start_date_ge/end_date_lt boundaries (Issue B)
builder.Services.Configure<ShopTimeZoneOptions>(builder.Configuration.GetSection(ShopTimeZoneOptions.SectionName));
builder.Services.AddSingleton<IShopTimeZone, ShopTimeZone>();
var dailySummaryTargets = builder.Configuration
    .GetSection(DailySummaryTargetOptions.SectionName)
    .Get<DailySummaryTargetOptions>() ?? new DailySummaryTargetOptions();
builder.Services.AddSingleton(dailySummaryTargets);
builder.Services.AddScoped<IDailySummaryService, DailySummaryService>();

// Register named HttpClients for TikTok API and Auth endpoints. Concrete services will be registered later.
builder.Services.AddHttpClient("TikTokApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["TikTok:BaseUrl"] ?? "https://open-api.tiktokglobalshop.com");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("TikTokAuth", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["TikTok:AuthBaseUrl"] ?? "https://auth.tiktok-shops.com");
    client.Timeout = TimeSpan.FromSeconds(30);
});


// Data protection (used to secure tokens at rest)
// Ensure Data Protection keys are persisted to a folder inside the application (not wwwroot)
// so encrypted tokens remain decryptable across application restarts/deploys.
var dpKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtection-Keys");
Directory.CreateDirectory(dpKeysPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
    .SetApplicationName("KaleContentOps");

// TikTok signature service (implementation pending - TODO: implement algorithm)
builder.Services.AddSingleton<ITikTokSignatureService, TikTokSignatureService>();
// TikTok auth and shop services
builder.Services.AddScoped<ITikTokAuthService, TikTokAuthService>();
builder.Services.AddScoped<ITikTokShopService, TikTokShopService>();
builder.Services.AddScoped<ITikTokVideoService, TikTokVideoService>();
// Details sync service (used by CLI and background worker)
builder.Services.AddScoped<TikTokDetailsSyncService>();

// Daily sync worker - register only when explicitly enabled via configuration (TikTok:EnableDailySync = true)
if (builder.Configuration.GetValue<bool>("TikTok:EnableDailySync", false))
{
    builder.Services.AddHostedService<TikTokDailySyncService>();
}

// Note: details-test diagnostic is executed after the app is built so DI is available.

// Resolve connection string explicitly and fail fast if missing for the current environment.
var defaultConnection = builder.Configuration.GetConnectionString("DefaultConnection");
if (builder.Environment.IsDevelopment())
{
    if (string.IsNullOrWhiteSpace(defaultConnection))
    {
        throw new InvalidOperationException("Development connection string 'DefaultConnection' is not configured. Set it in appsettings.Development.json or User Secrets.");
    }
}
else
{
    // In production, require a connection string to be set via environment variables on the host.
    if (string.IsNullOrWhiteSpace(defaultConnection))
    {
        throw new InvalidOperationException("Production connection string 'DefaultConnection' is not configured. Set environment variable 'ConnectionStrings__DefaultConnection' on the host.");
    }
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(defaultConnection));

var app = builder.Build();

// Development-only: run single details diagnostic if requested
if (args.Any(a => a != null && a.StartsWith("--details-test", StringComparison.OrdinalIgnoreCase)))
{
    // expected args: --details-test --shop=SHOP_CIPHER --video=VIDEO_ID
    string? shop = null;
    string? video = null;
    foreach (var a in args)
    {
        if (a != null && a.StartsWith("--shop=", StringComparison.OrdinalIgnoreCase)) shop = a.Substring("--shop=".Length);
        if (a != null && a.StartsWith("--video=", StringComparison.OrdinalIgnoreCase)) video = a.Substring("--video=".Length);
    }

    if (string.IsNullOrWhiteSpace(shop) || string.IsNullOrWhiteSpace(video))
    {
        Console.Error.WriteLine("--details-test requires --shop=SHOP_CIPHER and --video=VIDEO_ID");
        return;
    }

    using (var scope = app.Services.CreateScope())
    {
        var svc = scope.ServiceProvider.GetRequiredService<ITikTokVideoService>();
        Console.WriteLine($"=== TIKTOK VIDEO PERFORMANCE SINGLE TEST ===\nVideoId: {video}");
        try
        {
        // Use the per-video Details endpoint diagnostic which returns mapped engagement fields.
        var res = svc.RunDetailsDiagnosticAsync(video, shop).GetAwaiter().GetResult();
        if (res.StatusCode == null)
        {
            Console.WriteLine("TikTok credentials not configured for this app key");
            return;
        }

        Console.WriteLine($"HTTP Status: {res.StatusCode}");
        Console.WriteLine($"Endpoint: {res.Endpoint}");

        if (res.Root.HasValue && res.Data.HasValue)
        {
            var d = res.Data.Value;
            Console.WriteLine();
            Console.WriteLine($"Found video: {d.GetPropertyOrDefault("id") ?? d.GetPropertyOrDefault("video_id") ?? video}");

            // Parse details metrics using the ParseDetailsMetrics parser when available and report mapping status & JSON paths.
            KaleContentOps.Services.TikTok.DetailsMetrics metrics;
            try
            {
                if (svc is KaleContentOps.Services.TikTok.TikTokVideoService concrete)
                {
                    metrics = concrete.ParseDetailsMetrics(d);
                }
                else
                {
                    metrics = new KaleContentOps.Services.TikTok.DetailsMetrics();
                }
            }
            catch
            {
                metrics = new KaleContentOps.Services.TikTok.DetailsMetrics();
            }

            // Helper to print field mapping for nullable integers (use long? for aggregated metrics)
            void PrintField(string name, long? value, string? path)
            {
                if (value.HasValue)
                {
                    Console.WriteLine($"{name}: {value.Value} (mapped from {path})");
                }
                else if (!string.IsNullOrWhiteSpace(path))
                {
                    Console.WriteLine($"{name}: not provided by this endpoint (checked path {path})");
                }
                else
                {
                    Console.WriteLine($"{name}: NOT RETURNED");
                }
            }

            PrintField("Views", metrics.Views, metrics.ViewsPath);
            PrintField("Likes", metrics.Likes, metrics.LikesPath);
            PrintField("Comments", metrics.Comments, metrics.CommentsPath);
            PrintField("Shares", metrics.Shares, metrics.SharesPath);
            PrintField("NewFollowers", metrics.NewFollowers, metrics.NewFollowersPath);

            // AverageWatch, FullWatchRate, Reach may be optional or absent - print mapping info similarly.
            if (metrics.AverageWatch.HasValue)
            {
                Console.WriteLine($"AverageWatch: {metrics.AverageWatch.Value} (mapped from {metrics.AverageWatchPath})");
            }
            else if (!string.IsNullOrWhiteSpace(metrics.AverageWatchPath))
            {
                Console.WriteLine($"AverageWatch: not provided by this endpoint (checked path {metrics.AverageWatchPath})");
            }
            else
            {
                Console.WriteLine("AverageWatch: NOT RETURNED");
            }

            if (metrics.FullWatchRate.HasValue)
            {
                Console.WriteLine($"FullWatchRate: {metrics.FullWatchRate.Value} (mapped from {metrics.FullWatchRatePath})");
            }
            else if (!string.IsNullOrWhiteSpace(metrics.FullWatchRatePath))
            {
                Console.WriteLine($"FullWatchRate: not provided by this endpoint (checked path {metrics.FullWatchRatePath})");
            }
            else
            {
                Console.WriteLine("FullWatchRate: NOT RETURNED");
            }

            if (metrics.Reach.HasValue)
            {
                Console.WriteLine($"Reach: {metrics.Reach.Value} (mapped from {metrics.ReachPath})");
            }
            else if (!string.IsNullOrWhiteSpace(metrics.ReachPath))
            {
                Console.WriteLine($"Reach: not provided by this endpoint (checked path {metrics.ReachPath})");
            }
            else
            {
                Console.WriteLine("Reach: NOT RETURNED");
            }
        }
        else if (res.Root.HasValue)
        {
            Console.WriteLine("Details response did not contain data for the requested video");
        }
        else
        {
            Console.WriteLine("No JSON returned or parsing failed.");
        }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Performance diagnostic failed: {ex.Message}");
        }
    }

    return;
}

// Development-only dry-run runner: call with --dryrun [--shop=shopCipher] to execute DryRunVideoClassificationAsync and exit.
if (args.Contains("--dryrun"))
{
    // resolve shopCipher from args or configuration
    string? shopCipher = null;
    foreach (var a in args)
    {
        if (a != null && a.StartsWith("--shop=", StringComparison.OrdinalIgnoreCase))
        {
            shopCipher = a.Substring("--shop=".Length);
            break;
        }
    }

    if (string.IsNullOrWhiteSpace(shopCipher))
    {
        shopCipher = builder.Configuration["TikTok:ShopCipher"];
    }

    if (string.IsNullOrWhiteSpace(shopCipher))
    {
        Console.Error.WriteLine("Dry-run requires shop cipher. Provide --shop=SHOP_CIPHER or set TikTok:ShopCipher in configuration.");
        return;
    }

    using (var scope = app.Services.CreateScope())
    {
        var svc = scope.ServiceProvider.GetRequiredService<ITikTokVideoService>();
        Console.WriteLine($"Starting DryRunVideoClassificationAsync for shop {shopCipher} ...");
        var report = svc.DryRunVideoClassificationAsync(shopCipher, "2026-04-01", "2026-09-13").GetAwaiter().GetResult();

        Console.WriteLine("\n=== DRY RUN SUMMARY ===");
        Console.WriteLine($"TotalReceived: {report.Summary.TotalReceived}");
        Console.WriteLine($"TotalMatched: {report.Summary.TotalMatched}");
        Console.WriteLine($"TotalUnmatched: {report.Summary.TotalUnmatched}");
        Console.WriteLine($"WithProductsCount: {report.Summary.WithProductsCount}");
        Console.WriteLine($"WithoutProductsCount: {report.Summary.WithoutProductsCount}");
        Console.WriteLine($"WithGmvCount: {report.Summary.WithGmvCount}");
        Console.WriteLine($"WithItemsSoldCount: {report.Summary.WithItemsSoldCount}");
        Console.WriteLine($"WithSkuOrdersCount: {report.Summary.WithSkuOrdersCount}");

        Console.WriteLine("\n=== GROUND TRUTH ===");
        var groundTruth = new[] {
            "7683436081873800455",
            "7683467066388663559",
            "7683493477568515335",
            "7683510565985176840",
            "7683526456294640904",
            "7683563143968345364",
            "7683576265256783125"
        };

        foreach (var vid in groundTruth)
        {
            if (report.GroundTruthMatches.TryGetValue(vid, out var e))
            {
                Console.WriteLine("------------------------------");
                Console.WriteLine($"VideoId: {e.VideoId}");
                Console.WriteLine($"ContentLogId: {e.ContentLogId}");
                Console.WriteLine($"ExistingContentTypeId: {e.ExistingContentTypeId}");
                Console.WriteLine($"Title: {e.Title}");
                Console.WriteLine($"Username: {e.Username}");
                Console.WriteLine($"VideoPostTime: {e.VideoPostTime}");
                Console.WriteLine($"AuthorType: {e.AuthorType}");
                Console.WriteLine($"ProductsAvailable: {e.ProductsAvailable}");
                Console.WriteLine($"ProductsCount: {e.ProductsCount}");
                Console.WriteLine($"ProductNames: {string.Join(", ", e.ProductNames)}");
                Console.WriteLine($"GmvAmount: {e.GmvAmount}");
                Console.WriteLine($"GmvCurrency: {e.GmvCurrency}");
                Console.WriteLine($"ItemsSold: {e.ItemsSold}");
                Console.WriteLine($"SkuOrders: {e.SkuOrders}");
                Console.WriteLine($"HashTags: {string.Join(", ", e.HashTags)}");
                Console.WriteLine($"PropertyNames: {string.Join(", ", e.PropertyNames)}");
            }
            else
            {
                Console.WriteLine($"NOT FOUND: {vid}");
            }
        }

        Console.WriteLine("\n=== GROUND TRUTH CLASSIFICATION COMPARISON ===");
        Console.WriteLine("VideoId | ExistingContentTypeId | ProductsCount | GMV | ItemsSold | SkuOrders | Title");
        foreach (var vid in groundTruth)
        {
            if (report.GroundTruthMatches.TryGetValue(vid, out var e))
            {
                Console.WriteLine($"{e.VideoId} | {e.ExistingContentTypeId} | {e.ProductsCount} | {e.GmvAmount.GetValueOrDefault():0.##} | {e.ItemsSold.GetValueOrDefault()} | {e.SkuOrders.GetValueOrDefault()} | {e.Title}");
            }
            else
            {
                Console.WriteLine($"{vid} | NOT FOUND");
            }
        }
    }

    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseSession();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();
}

    // Development-only: details sync CLI
    if (args.Any(a => a != null && a.StartsWith("--details-sync", StringComparison.OrdinalIgnoreCase)))
    {
        // expected args: --details-sync --shop=SHOP_CIPHER --limit=10 [--skip=0]
        string? shop = null;
        int limit = 10;
        int skip = 0;
        foreach (var a in args)
        {
            if (a != null && a.StartsWith("--shop=", StringComparison.OrdinalIgnoreCase)) shop = a.Substring("--shop=".Length);
            if (a != null && a.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && int.TryParse(a.Substring("--limit=".Length), out var l)) limit = l;
            if (a != null && a.StartsWith("--skip=", StringComparison.OrdinalIgnoreCase) && int.TryParse(a.Substring("--skip=".Length), out var s)) skip = s;
        }

        if (string.IsNullOrWhiteSpace(shop))
        {
            Console.Error.WriteLine("--details-sync requires --shop=SHOP_CIPHER");
            return;
        }

        using (var scope = app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<TikTokDetailsSyncService>();
            Console.WriteLine($"=== TIKTOK DETAILS SYNC === shop={shop} limit={limit} skip={skip}");
            var processed = svc.RunDetailsSyncAsync(shop, limit, skip).GetAwaiter().GetResult();
            Console.WriteLine($"Details sync processed {processed} videos");
        }

        return;
    }

app.Run();