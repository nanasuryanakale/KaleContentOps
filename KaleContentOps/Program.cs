using KaleContentOps.Data;
using Microsoft.EntityFrameworkCore;
using KaleContentOps.Services.TikTok;

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

// TikTok signature service (implementation pending - TODO: implement algorithm)
builder.Services.AddSingleton<ITikTokSignatureService, TikTokSignatureService>();
// TikTok auth and shop services
builder.Services.AddScoped<ITikTokAuthService, TikTokAuthService>();
builder.Services.AddScoped<ITikTokShopService, TikTokShopService>();
builder.Services.AddScoped<ITikTokVideoService, TikTokVideoService>();

// Data protection (used to secure tokens at rest)
builder.Services.AddDataProtection();

// Daily sync worker - register only when explicitly enabled via configuration (TikTok:EnableDailySync = true)
if (builder.Configuration.GetValue<bool>("TikTok:EnableDailySync", false))
{
    builder.Services.AddHostedService<TikTokDailySyncService>();
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

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

app.Run();