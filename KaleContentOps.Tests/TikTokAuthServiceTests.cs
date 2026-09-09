using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace KaleContentOps.Tests;

internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public FakeHttpMessageHandler(HttpResponseMessage response)
    {
        _response = response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        return Task.FromResult(_response);
    }
}

public class TikTokAuthServiceTests
{
    private TikTokAuthService CreateWithResponse(string responseJson)
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler)
        {
            BaseAddress = new System.Uri("https://auth.tiktok-shops.com")
        };
        var factory = new SimpleHttpClientFactory(client);

        var options = Options.Create(new TikTokOptions { AppKey = "appKey", AppSecret = "appSecret" });

        var optionsBuilder = new DbContextOptionsBuilder<KaleContentOps.Data.AppDbContext>();
        optionsBuilder.UseInMemoryDatabase("testdb");
        var db = new KaleContentOps.Data.AppDbContext(optionsBuilder.Options);

        var dpProvider = DataProtectionProvider.Create("TikTokTests");

        return new TikTokAuthService(factory, options, db, dpProvider);
    }

    [Fact]
    public async Task ExchangeAuthCode_Parses_Success_Response()
    {
        var json = "{ \"code\":0, \"data\":{ \"access_token\":\"ATOKEN\", \"access_token_expire_in\":1672531200, \"refresh_token\":\"RTOKEN\", \"refresh_token_expire_in\":1675123200, \"open_id\":\"OID\", \"seller_name\":\"Seller\", \"seller_base_region\":\"ID\", \"user_type\":\"SELLER\", \"granted_scopes\":\"data.shop_analytics.public.read\" } }";
        var svc = CreateWithResponse(json);

        var res = await svc.ExchangeAuthCodeAsync("authcode");
        Assert.NotNull(res);
        Assert.Equal("ATOKEN", res.AccessToken);
        Assert.Equal("RTOKEN", res.RefreshToken);
        Assert.Equal("OID", res.OpenId);
        Assert.Equal("Seller", res.SellerName);
    }

    [Fact]
    public async Task ExchangeAuthCode_Throws_On_Missing_Scope()
    {
        var json = "{ \"code\":0, \"data\":{ \"access_token\":\"ATOKEN\", \"access_token_expire_in\":1672531200, \"refresh_token\":\"RTOKEN\", \"refresh_token_expire_in\":1675123200, \"granted_scopes\":\"other.scope\" } }";
        var svc = CreateWithResponse(json);

        await Assert.ThrowsAsync<TikTokAuthException>(async () => await svc.ExchangeAuthCodeAsync("authcode"));
    }

    [Fact]
    public async Task ExchangeAuthCode_Throws_On_NonZero_Code()
    {
        var json = "{ \"code\":400, \"message\":\"bad request\" }";
        var svc = CreateWithResponse(json);

        var ex = await Assert.ThrowsAsync<TikTokAuthException>(async () => await svc.ExchangeAuthCodeAsync("authcode"));
        Assert.Contains("bad request", ex.Message);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_Updates_Credential()
    {
        var json = "{ \"code\":0, \"data\":{ \"access_token\":\"NEWAT\", \"access_token_expire_in\":1672531200, \"refresh_token\":\"NEWRT\", \"refresh_token_expire_in\":1675123200, \"granted_scopes\":\"data.shop_analytics.public.read\" } }";
        var svc = CreateWithResponse(json);

        // create credential
        var optionsBuilder = new DbContextOptionsBuilder<KaleContentOps.Data.AppDbContext>();
        optionsBuilder.UseInMemoryDatabase("testdb2");
        var db = new KaleContentOps.Data.AppDbContext(optionsBuilder.Options);
        var cred = new TikTokCredential { AppKey = "appKey" };
        // seed a refresh token so refresh call can proceed
        var protector = DataProtectionProvider.Create("TikTokTests").CreateProtector("TikTokAuthService.v1");
        cred.EncryptedRefreshToken = protector.Protect("RTOKEN");
        db.TikTokCredentials.Add(cred);
        await db.SaveChangesAsync();

        // use a service that points to same db
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("https://auth.tiktok-shops.com") };
        var factory = new SimpleHttpClientFactory(client);
        var options = Options.Create(new TikTokOptions { AppKey = "appKey", AppSecret = "appSecret" });
        var dp = DataProtectionProvider.Create("TikTokTests");
        var svc2 = new TikTokAuthService(factory, options, db, dp);

        var result = await svc2.RefreshAccessTokenAsync(cred.Id);
        Assert.NotNull(result);

        var updated = await db.TikTokCredentials.FindAsync(cred.Id);
        Assert.NotNull(updated?.EncryptedAccessToken);
    }
}

internal class SimpleHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;
    public SimpleHttpClientFactory(HttpClient client) => _client = client;
    public HttpClient CreateClient(string name) => _client;
}
