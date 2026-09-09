using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services.TikTok;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace KaleContentOps.Tests;

internal class FakeHttpHandlerForResponse : HttpMessageHandler
{
    private readonly string _responseContent;
    private readonly HttpStatusCode _statusCode;
    public HttpRequestMessage? LastRequest { get; private set; }
    public FakeHttpHandlerForResponse(HttpResponseMessage res)
    {
        _responseContent = res.Content == null ? string.Empty : res.Content.ReadAsStringAsync().Result;
        _statusCode = res.StatusCode;
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        LastRequest = request;
        var newResp = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(newResp);
    }
}

internal class FakeSignatureService : ITikTokSignatureService
{
    public string? LastPath { get; private set; }
    public IDictionary<string, string?>? LastQuery { get; private set; }
    public byte[]? LastBody { get; private set; }
    public string? LastContentType { get; private set; }
    private readonly string _sig;
    public FakeSignatureService(string sig = "mocksignature") => _sig = sig;
    public string GenerateSignature(string httpMethod, string path, IDictionary<string, string?> queryParams, byte[]? bodyBytes, string? contentType)
    {
        LastPath = path;
        LastQuery = new Dictionary<string, string?>(queryParams);
        LastBody = bodyBytes;
        LastContentType = contentType;
        return _sig;
    }
}

internal class FakeAuthService : ITikTokAuthService
{
    private readonly string _token;
    public FakeAuthService(string token) => _token = token;

    public Task<TikTokTokenResponse?> ExchangeAuthCodeAsync(string authCode, System.Threading.CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TikTokTokenResponse?> RefreshTokenAsync(TikTokCredential credential, System.Threading.CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TikTokTokenResponse?> RefreshAccessTokenAsync(long credentialId, System.Threading.CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<string?> GetValidAccessTokenAsync(long credentialId, System.Threading.CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(_token);
    }
}

public class TikTokShopServiceTests
{
    private TikTokShopService CreateServiceWithResponse(string jsonResponse, out FakeHttpHandlerForResponse handler, out FakeSignatureService sigService, out FakeAuthService authService, out AppDbContext db)
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        };
        handler = new FakeHttpHandlerForResponse(res);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://open-api.tiktokglobalshop.com") };
        var factory = new SimpleHttpClientFactory(client);

        sigService = new FakeSignatureService("signedval");
        authService = new FakeAuthService("ACCESSTOKEN");

        var options = Options.Create(new TikTokOptions { AppKey = "appKey", AppSecret = "appSecret" });

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseInMemoryDatabase(Guid.NewGuid().ToString());
        db = new AppDbContext(optionsBuilder.Options);

        // seed a TikTokCredential row for appKey
        var cred = new TikTokCredential { AppKey = "appKey", EncryptedRefreshToken = null };
        db.TikTokCredentials.Add(cred);
        db.SaveChanges();

        return new TikTokShopService(factory, options, db, authService, sigService);
    }

    [Fact]
    public async Task GetAuthorizedShops_Parses_Success_Response()
    {
        var json = "{ \"code\":0, \"data\":{ \"shops\":[{ \"shop_cipher\":\"C1\", \"shop_id\":\"100\", \"shop_code\":\"SC\", \"shop_name\":\"ShopName\", \"region\":\"ID\", \"seller_type\":\"SELLER\" }] } }";
        var svc = CreateServiceWithResponse(json, out var handler, out var sig, out var auth, out var db);

        var result = await svc.FetchAndSaveAuthorizedShopsAsync();
        Assert.NotNull(result);
        Assert.Single(result);
        var shop = result![0];
        Assert.Equal("C1", shop.ShopCipher);
        Assert.Equal("100", shop.ShopId);
        // verify DB persisted
        var persisted = await db.TikTokShops.FirstOrDefaultAsync();
        Assert.NotNull(persisted);
        Assert.Equal("C1", persisted.ShopCipher);
    }

    [Fact]
    public async Task GetAuthorizedShops_Handles_Empty_Shops()
    {
        var json = "{ \"code\":0, \"data\":{ \"shops\":[] } }";
        var svc = CreateServiceWithResponse(json, out var handler, out var sig, out var auth, out var db);

        var result = await svc.FetchAndSaveAuthorizedShopsAsync();
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAuthorizedShops_Throws_On_NonZero_Code()
    {
        var json = "{ \"code\":400, \"message\":\"bad\" }";
        var svc = CreateServiceWithResponse(json, out var handler, out var sig, out var auth, out var db);

        await Assert.ThrowsAsync<TikTokAuthException>(async () => await svc.FetchAndSaveAuthorizedShopsAsync());
    }

    [Fact]
    public async Task GetAuthorizedShops_Throws_On_NonSuccessHttp()
    {
        // create handler that returns 500
        var res = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var handler = new FakeHttpHandlerForResponse(res);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://open-api.tiktokglobalshop.com") };
        var factory = new SimpleHttpClientFactory(client);
        var sig = new FakeSignatureService("s");
        var auth = new FakeAuthService("t");
        var options = Options.Create(new TikTokOptions { AppKey = "appKey", AppSecret = "appSecret" });
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseInMemoryDatabase(Guid.NewGuid().ToString());
        var db = new AppDbContext(optionsBuilder.Options);
        db.TikTokCredentials.Add(new TikTokCredential { AppKey = "appKey" });
        db.SaveChanges();

        var svc = new TikTokShopService(factory, options, db, auth, sig);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await svc.FetchAndSaveAuthorizedShopsAsync());
    }

    [Fact]
    public async Task GetAuthorizedShops_Upserts_Shop_And_DoesNot_Create_Duplicate()
    {
        var json = "{ \"code\":0, \"data\":{ \"shops\":[{ \"shop_cipher\":\"C2\", \"shop_id\":\"200\", \"shop_code\":\"SC2\", \"shop_name\":\"Shop2\", \"region\":\"ID\", \"seller_type\":\"SELLER\" }] } }";
        var svc = CreateServiceWithResponse(json, out var handler, out var sig, out var auth, out var db);

        var r1 = await svc.FetchAndSaveAuthorizedShopsAsync();
        var r2 = await svc.FetchAndSaveAuthorizedShopsAsync();
        var all = await db.TikTokShops.ToListAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task AccessTokenHeader_And_Signature_Called_Correctly()
    {
        var json = "{ \"code\":0, \"data\":{ \"shops\":[] } }";
        var svc = CreateServiceWithResponse(json, out var handler, out var sigService, out var authService, out var db);

        var result = await svc.FetchAndSaveAuthorizedShopsAsync();

        // verify signature service got path
        Assert.Equal("/authorization/202309/shops", sigService.LastPath);
        // verify sign input did not contain 'sign' or 'access_token'
        Assert.False(sigService.LastQuery!.ContainsKey("sign"));
        Assert.False(sigService.LastQuery!.ContainsKey("access_token"));

        // verify header used
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.Contains("x-tts-access-token"));
        var token = System.Linq.Enumerable.First(handler.LastRequest.Headers.GetValues("x-tts-access-token"));
        Assert.Equal("ACCESSTOKEN", token);
    }
}

// Reuse SimpleHttpClientFactory from other test file
