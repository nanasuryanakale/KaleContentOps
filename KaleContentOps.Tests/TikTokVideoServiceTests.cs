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
using KaleContentOps.Services.TikTok;
using Xunit;

namespace KaleContentOps.Tests;

public class TikTokVideoServiceTests
{
    // Basic fake HTTP handler that returns provided JSON content
    internal class SimpleHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public HttpRequestMessage? LastRequest { get; private set; }
        public SimpleHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _json = json; _status = status;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var resp = new HttpResponseMessage(_status) { Content = new StringContent(_json, Encoding.UTF8, "application/json") };
            return Task.FromResult(resp);
        }
    }

    private AppDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(opts);
    }

    [Fact]
    public async Task VideoList_Parses_And_Upserts()
    {
        var db = CreateDb();
        // Seed credential and shop
        var cred = new TikTokCredential { AppKey = "APPKEY" };
        db.TikTokCredentials.Add(cred);
        var shop = new TikTokShop { ShopCipher = "SC" };
        db.TikTokShops.Add(shop);
        db.SaveChanges();

        var json = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new
            {
                videos = new[] {
                    new {
                        video_id = "123",
                        title = "Test",
                        username = "u",
                        video_post_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        duration = 10,
                        creator = new { open_id = "oid", user_name = "u", nick_name = "nick", author_type = "OFFICIAL" }
                    }
                }
            }
        });

        var handler = new SimpleHandler(json);
        var clientFactory = new SimpleHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://open-api.tiktokglobalshop.com") });

        var opts = Options.Create(new TikTokOptions { AppKey = "APPKEY" });

        // use real signature and auth services from existing code; but stub auth to return token
        var sig = new TikTokSignatureService(Options.Create(new TikTokOptions { AppSecret = "SECRET" }));
        var auth = new FakeAuthService(() => Task.FromResult<string?>("ACCESSTOKEN"));

        var svc = new TikTokVideoService(clientFactory, opts, db, auth, sig);
        var count = await svc.FetchAndSaveVideoListAsync("SC");

        Assert.Equal(1, count);
        var entry = await db.ContentLogs.SingleAsync();
        Assert.Equal("123", entry.VideoId);
    }

    // Fake auth service to return a constant token
    class FakeAuthService : ITikTokAuthService
    {
        private readonly Func<Task<string?>> _getter;
        public FakeAuthService(Func<Task<string?>> getter) => _getter = getter;
        public Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
        public Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
        public Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => Task.FromResult<TikTokTokenResponse?>(null);
        public Task<string?> GetValidAccessTokenAsync(long credentialId, CancellationToken cancellationToken = default) => _getter();
    }

}
