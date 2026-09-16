using KaleContentOps.Data;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Services.TikTok;
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

// Daily sync worker - register only when explicitly enabled via configuration (TikTok:EnableDailySync = true)
if (builder.Configuration.GetValue<bool>("TikTok:EnableDailySync", false))
{
    builder.Services.AddHostedService<TikTokDailySyncService>();
}

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

app.Run();