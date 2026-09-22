using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using KaleContentOps.Services.DailySummary;
using KaleContentOps.Services.TikTok;
using KaleContentOps.ViewModels;
using Xunit;

namespace KaleContentOps.Tests;

public class ShopTimeZoneTests
{
    private const string Jakarta = ShopTimeZoneOptions.DefaultTimeZoneId; // Asia/Jakarta, GMT+7, no DST

    private static IShopTimeZone CreateDefault() =>
        new ShopTimeZone(Options.Create(new ShopTimeZoneOptions { TimeZoneId = Jakarta }));

    /// <summary>Creates the UTC DateTimeOffset for a Jakarta wall-clock time (GMT+7, no DST).</summary>
    private static DateTimeOffset UtcFromJakarta(int y, int mo, int d, int h, int mi, int s = 0) =>
        new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero).AddHours(-7);

    // ============================================================
    // Issue A: Daily Summary default "today" = shop-local date
    // ============================================================

    [Fact]
    public void Jakarta_2026_09_22_0030_Today_Is_2026_09_22()
    {
        // 2026-09-22 00:30 WIB == 2026-09-21 17:30 UTC
        var instant = UtcFromJakarta(2026, 9, 22, 0, 30, 0);
        Assert.Equal(new DateTime(2026, 9, 21, 17, 30, 0), instant.UtcDateTime);

        var tz = CreateDefault();
        Assert.Equal(new DateOnly(2026, 9, 22), tz.GetShopLocalDate(instant));
        Assert.Equal(new DateTime(2026, 9, 22), tz.TodayMidnight().Date); // same calendar date
    }

    [Fact]
    public void SameInstant_InUtc_Still_Yields_ShopLocal_2026_09_22()
    {
        // Same instant expressed in UTC: 2026-09-21 17:30 UTC
        var instant = new DateTimeOffset(2026, 9, 21, 17, 30, 0, TimeSpan.Zero);
        var tz = CreateDefault();

        // Daily Summary "today" must be 2026-09-22 (shop local), not 2026-09-21 (UTC date).
        Assert.Equal(new DateOnly(2026, 9, 22), tz.GetShopLocalDate(instant));
        Assert.NotEqual(instant.Date, tz.GetShopLocalDate(instant).ToDateTime(TimeOnly.MinValue));
    }

    [Fact]
    public void MidnightBoundary_Jakarta_2026_09_22_0000_Is_2026_09_22()
    {
        var instant = UtcFromJakarta(2026, 9, 22, 0, 0, 0); // 2026-09-21 17:00 UTC
        var tz = CreateDefault();
        Assert.Equal(new DateOnly(2026, 9, 22), tz.GetShopLocalDate(instant));
    }

    [Fact]
    public void JustBeforeMidnight_Jakarta_2026_09_21_235959_Is_2026_09_21()
    {
        var instant = UtcFromJakarta(2026, 9, 21, 23, 59, 59); // 2026-09-21 16:59:59 UTC
        var tz = CreateDefault();
        Assert.Equal(new DateOnly(2026, 9, 21), tz.GetShopLocalDate(instant));
    }

    [Fact]
    public void ToShopLocal_Converts_Utc_Instant_To_Jakarta_WallClock()
    {
        var instant = new DateTimeOffset(2026, 9, 21, 17, 30, 0, TimeSpan.Zero);
        var local = CreateDefault().ToShopLocal(instant);
        Assert.Equal(TimeSpan.FromHours(7), local.Offset);
        Assert.Equal(new DateTime(2026, 9, 22, 0, 30, 0), local.DateTime);
    }

    // ============================================================
    // Issue B: TikTok fetch window built from shop-local calendar
    // ============================================================

    [Fact]
    public void FetchWindow_WhenShopLocalTodayIs_2026_09_22_EndDateLt_Is_2026_09_23_And_Start_Is_30_Calendar_Days_Back()
    {
        // Shop-local today: 2026-09-22 (current instant 2026-09-22 03:00 WIB == 2026-09-21 20:00 UTC)
        var nowUtc = UtcFromJakarta(2026, 9, 22, 3, 0, 0);
        var tz = CreateDefault();

        var todayMidnight = tz.ToDateTime(tz.GetShopLocalDate(nowUtc));   // 2026-09-22 00:00
        var endDateLt = todayMidnight.AddDays(1);                         // end_date_lt (exclusive)
        var startDateGe = todayMidnight.AddDays(-30);                     // existing 30-day rule

        Assert.Equal(new DateTime(2026, 9, 23), endDateLt);
        Assert.Equal(new DateTime(2026, 8, 23), startDateGe);

        // API date strings are pure ISO dates from the shop-local calendar (no +7h arithmetic)
        Assert.Equal("2026-09-23", endDateLt.ToString("yyyy-MM-dd"));
        Assert.Equal("2026-08-23", startDateGe.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task FetchAndSaveVideoListAsync_Sends_ShopLocal_Dates_Not_Utc_Dates()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.TikTokCredentials.Add(new TikTokCredential { AppKey = "APPKEY" });
        db.TikTokShops.Add(new TikTokShop { ShopCipher = "SC" });
        db.SaveChanges();

        var json = JsonSerializer.Serialize(new { code = 0, data = new { videos = Array.Empty<object>() } });
        var handler = new CapturingHandler(json);
        var clientFactory = new SimpleHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://open-api.tiktokglobalshop.com") });

        var sig = new TikTokSignatureService(Options.Create(new TikTokOptions { AppSecret = "SECRET" }));
        var auth = new StubAuthService();
        var tz = CreateDefault();

        var svc = new TikTokVideoService(
            clientFactory,
            Options.Create(new TikTokOptions { AppKey = "APPKEY" }),
            db, auth, sig,
            shopTimeZone: tz);

        await svc.FetchAndSaveVideoListAsync("SC");

        Assert.NotNull(handler.LastRequest);
        var qs = System.Web.HttpUtility.ParseQueryString(handler.LastRequest!.RequestUri!.Query);

        var expectedToday = tz.TodayMidnight();
        // end_date_lt must be shop-local tomorrow, not UTC tomorrow.
        Assert.Equal(expectedToday.AddDays(1).ToString("yyyy-MM-dd"), qs["end_date_lt"]);
        Assert.Equal(expectedToday.AddDays(-30).ToString("yyyy-MM-dd"), qs["start_date_ge"]);

        // Explicit edge: with shop-local today == 2026-09-22 the window must be 08-23 -> 09-23.
        Assert.Equal("2026-09-23", qs["end_date_lt"]);
        Assert.Equal("2026-08-23", qs["start_date_ge"]);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _json;
        public HttpRequestMessage? LastRequest { get; private set; }
        public CapturingHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    private sealed class StubAuthService : ITikTokAuthService
    {
        public Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default)
            => Task.FromResult<TikTokTokenResponse?>(null);
        public Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult<TikTokTokenResponse?>(null);
        public Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default)
            => Task.FromResult<TikTokTokenResponse?>(null);
        public Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("ACCESSTOKEN");
    }

    // ============================================================
    // Issue A: controller default end date uses shop-local today
    // ============================================================

    [Fact]
    public async Task DailySummaryController_Default_Today_Is_ShopLocalDate()
    {
        var expectedToday = CreateDefault().TodayMidnight().Date;

        var controller = new KaleContentOps.Controllers.DailySummaryController(
            new StubDailySummaryService(),
            CreateDefault());

        var result = await controller.Index(null, null, cancellationToken: CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result);
        // The service received the filter built by the controller; assert on the captured filter.
        Assert.NotNull(StubDailySummaryService.LastFilter);
        Assert.Equal(expectedToday, StubDailySummaryService.LastFilter!.EndDate);
        Assert.Equal(expectedToday.AddDays(-6), StubDailySummaryService.LastFilter.StartDate);
    }

    private sealed class StubDailySummaryService : IDailySummaryService
    {
        public static DailySummaryFilter? LastFilter { get; private set; }

        public Task<DailySummaryPageModel> BuildAsync(DailySummaryFilter filter, CancellationToken cancellationToken = default)
        {
            LastFilter = filter;
            return Task.FromResult(new DailySummaryPageModel());
        }
    }
}
