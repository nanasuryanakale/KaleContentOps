using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using KaleContentOps.Services.TikTok;
using Xunit;

namespace KaleContentOps.Tests;

public class TikTokSignatureServiceTests
{
    private ITikTokSignatureService Create(string appSecret)
    {
        var opts = Options.Create(new TikTokOptions { AppSecret = appSecret });
        return new TikTokSignatureService(opts);
    }

    [Fact]
    public void Signature_Excludes_Sign_And_AccessToken_And_Sorts_Keys()
    {
        var svc = Create("mysecret");
        var path = "/authorization/202309/shops";
        var queries = new Dictionary<string, string?>
        {
            { "b", "2" },
            { "sign", "SHOULD_BE_EXCLUDED" },
            { "a", "1" },
            { "access_token", "TOKEN" }
        };

        // No body
        var result = svc.GenerateSignature("GET", path, queries, null, null);

        // Recompute expected using same algorithm here for deterministic test
        var expected = ComputeExpected("mysecret", path, new Dictionary<string, string?> { { "a","1" }, { "b","2" } }, null);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Signature_Includes_Body_For_NonMultipart()
    {
        var svc = Create("sekret");
        var path = "/test/path";
        var queries = new Dictionary<string, string?> { { "z", "9" } };
        var body = Encoding.UTF8.GetBytes("{\"name\":\"value\"}");
        var contentType = "application/json";

        var result = svc.GenerateSignature("POST", path, queries, body, contentType);
        var expected = ComputeExpected("sekret", path, queries, body);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Signature_Excludes_Body_For_Multipart()
    {
        var svc = Create("mypass");
        var path = "/upload";
        var queries = new Dictionary<string, string?> { { "x","1" } };
        var body = Encoding.UTF8.GetBytes("binarydata");
        var contentType = "multipart/form-data; boundary=----WebKitFormBoundary";

        var result = svc.GenerateSignature("POST", path, queries, body, contentType);
        var expected = ComputeExpected("mypass", path, queries, null);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Signature_Returns_Lowercase_Hex()
    {
        var svc = Create("abc123");
        var path = "/p";
        var queries = new Dictionary<string, string?> { { "k","v" } };

        var sig = svc.GenerateSignature("GET", path, queries, null, null);
        Assert.Matches("^[0-9a-f]+$", sig);
    }

    [Fact]
    public void Signature_Official_TikTok_Sample_GetAuthorizedShops()
    {
        // Official TikTok sample vector from "Sign Your API Request" docs
        // app_secret: e59af819cc
        // path: /authorization/202309/shops
        // query: app_key=29a39d, timestamp=1623812664
        // expected signature (official): b596b73e0cc6de07ac26f036364178ab16b0a907af13d43f0a0cd2345f582dc8
        var svc = Create("e59af819cc");
        var path = "/authorization/202309/shops";
        var queries = new Dictionary<string, string?>
        {
            { "app_key", "29a39d" },
            { "timestamp", "1623812664" }
        };

        var sig = svc.GenerateSignature("GET", path, queries, null, null);

        // Assert against literal official expected signature (do not compute expected here)
        Assert.Equal("b596b73e0cc6de07ac26f036364178ab16b0a907af13d43f0a0cd2345f582dc8", sig);
    }

    private static string ComputeExpected(string appSecret, string path, Dictionary<string, string?> queries, byte[]? body)
    {
        var utf8 = Encoding.UTF8;
        // sort keys
        var ordered = queries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        using var ms = new System.IO.MemoryStream();
        ms.Write(utf8.GetBytes(path));
        foreach (var k in ordered)
        {
            var v = queries[k] ?? string.Empty;
            ms.Write(utf8.GetBytes(string.Concat(k, v)));
        }
        if (body != null)
            ms.Write(body, 0, body.Length);

        var prefix = utf8.GetBytes(appSecret);
        using var final = new System.IO.MemoryStream();
        final.Write(prefix, 0, prefix.Length);
        final.Write(ms.ToArray(), 0, (int)ms.Length);
        final.Write(prefix, 0, prefix.Length);
        var message = final.ToArray();

        var hash = new System.Security.Cryptography.HMACSHA256(utf8.GetBytes(appSecret)).ComputeHash(message);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }
}
