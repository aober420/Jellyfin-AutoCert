using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.AutoCert;
using Xunit;

public class ClassicGoDaddyTests
{
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreatesWithKeyPairAndPreservesOtherTxtValues(bool hasOtherRecord)
    {
        var c = BehaviorTests.Config(); c.GoDaddyAuthMode = "Classic";
        var calls = 0;
        using var handler = new Handler(async r =>
        {
            Assert.Equal("sso-key production-key:production-secret", r.Headers.Authorization!.ToString());
            Assert.Equal("api.godaddy.com", r.RequestUri!.Host);
            switch (calls++)
            {
                case 0:
                    Assert.Equal(HttpMethod.Patch, r.Method);
                    Assert.Equal("/v1/domains/example.com/records", r.RequestUri.AbsolutePath);
                    using (var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()))
                    { Assert.Equal(1, body.RootElement.GetArrayLength()); Assert.Equal("our-value", body.RootElement[0].GetProperty("data").GetString()); Assert.Equal("_acme-challenge.jellyfin", body.RootElement[0].GetProperty("name").GetString()); }
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case 1:
                    Assert.Equal(HttpMethod.Get, r.Method);
                    Assert.Equal("/v1/domains/example.com/records/TXT/_acme-challenge.jellyfin", r.RequestUri.AbsolutePath);
                    var records = hasOtherRecord ? new[] { new { type = "TXT", name = "_acme-challenge.jellyfin", data = "our-value", ttl = 600 }, new { type = "TXT", name = "_acme-challenge.jellyfin", data = "someone-elses-value", ttl = 3600 } } : new[] { new { type = "TXT", name = "_acme-challenge.jellyfin", data = "our-value", ttl = 600 } };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(records)) };
                default:
                    Assert.Equal("/v1/domains/example.com/records/TXT/_acme-challenge.jellyfin", r.RequestUri.AbsolutePath);
                    Assert.Equal(hasOtherRecord ? HttpMethod.Put : HttpMethod.Delete, r.Method);
                    if (hasOtherRecord)
                    {
                        using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
                        Assert.Equal(1, body.RootElement.GetArrayLength());
                        Assert.Equal("someone-elses-value", body.RootElement[0].GetProperty("data").GetString());
                        Assert.Equal(3600, body.RootElement[0].GetProperty("ttl").GetInt32());
                    }
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        });
        using var dns = new DnsProvider(c, "production-key:production-secret", handler);
        var lease = await dns.Create("our-value", default);
        await dns.Delete(lease, default); Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(200, "[]")]
    [InlineData(404, "")]
    [InlineData(200, "[{\"type\":\"TXT\",\"name\":\"_acme-challenge.jellyfin\",\"data\":\"different\",\"ttl\":600}]")]
    public async Task DoesNotMutateWhenOurRecordIsAlreadyAbsent(int status, string json)
    {
        var c = BehaviorTests.Config(); c.GoDaddyAuthMode = "Classic";
        using var handler = new Handler(r =>
        { Assert.Equal(HttpMethod.Get, r.Method); return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json) }); });
        using var dns = new DnsProvider(c, "key:secret", handler);
        await dns.Delete(new DnsLease("GoDaddy", "example.com", "", "", "our-value", "_acme-challenge.jellyfin"), default);
    }

    [Fact]
    public async Task RejectsUnrelatedRecordsBeforeWriting()
    {
        var c = BehaviorTests.Config(); c.GoDaddyAuthMode = "Classic";
        using var handler = new Handler(r =>
        { Assert.Equal(HttpMethod.Get, r.Method); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"type\":\"TXT\",\"name\":\"unrelated\",\"data\":\"our-value\"}]") }); });
        using var dns = new DnsProvider(c, "key:secret", handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dns.Delete(new DnsLease("GoDaddy", "example.com", "", "", "our-value", "_acme-challenge.jellyfin"), default));
    }
}
