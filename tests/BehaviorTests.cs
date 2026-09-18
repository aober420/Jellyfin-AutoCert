using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AutoCert;
using Jellyfin.Plugin.AutoCert.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public class BehaviorTests
{
    public static PluginConfiguration Config() => new() { Domain = "jellyfin.example.com", Zone = "example.com", Email = "admin@example.com", Enabled = true, AcceptTerms = true };
    [Theory]
    [InlineData("https://example.com")][InlineData("example.com:443")][InlineData("*.example.com")][InlineData("bad..example.com")][InlineData("-bad.example.com")][InlineData("example.com/path")]
    public void RejectsNonHostnames(string host) => Assert.ThrowsAny<Exception>(() => Validation.Host(host));
    [Fact]
    public void RejectsSuffixSpoofing() { var c = Config(); c.Domain = "notexample.com"; Assert.Throws<InvalidOperationException>(() => Validation.Check(c)); }
    [Fact]
    public void NormalizesHostnames() => Assert.Equal("jellyfin.example.com", Validation.Host(" Jellyfin.Example.COM. "));
    [Theory]
    [InlineData("example.com", "_acme-challenge")]
    [InlineData("jellyfin.example.com", "_acme-challenge.jellyfin")]
    [InlineData("jellyfin.home.example.com", "_acme-challenge.jellyfin.home")]
    public void PlacesChallengeInCorrectZone(string host, string expected) => Assert.Equal(expected, Validation.RelativeName(host, "example.com"));
    [Fact]
    public void RenewsShortCertificatesAtOneThirdLifetime()
    {
        var end = DateTimeOffset.UtcNow.AddDays(20); var start = end.AddDays(-45);
        Assert.False(Validation.Due(DateTimeOffset.UtcNow, start, end, 30));
        Assert.True(Validation.Due(end.AddDays(-14), start, end, 30));
        Assert.True(Validation.Due(end.AddDays(1), start, end, 30));
    }

    static byte[] Pfx(string domain, string password, bool expired = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + domain, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(domain); request.CertificateExtensions.Add(names.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 90));
        return cert.Export(X509ContentType.Pkcs12, password);
    }
    [Fact]
    public void RejectsWrongPasswordAndHostnameAndExpiredCertificate()
    {
        var bytes = Pfx("jellyfin.example.com", "correct-password");
        Assert.ThrowsAny<CryptographicException>(() => Validation.CheckPfx(bytes, "wrong", "jellyfin.example.com"));
        Assert.Throws<InvalidOperationException>(() => Validation.CheckPfx(bytes, "correct-password", "other.example.com"));
        Assert.Throws<InvalidOperationException>(() => Validation.CheckPfx(Pfx("jellyfin.example.com", "password", true), "password", "jellyfin.example.com"));
        using var cert = Validation.CheckPfx(bytes, "correct-password", "jellyfin.example.com"); Assert.True(cert.HasPrivateKey);
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request); }

    [Theory]
    [InlineData("GoDaddy", "{\"recordId\":\"created-123\"}", "/v3/domains/zones/example.com/dns-records")]
    [InlineData("Cloudflare", "{\"success\":true,\"result\":{\"id\":\"created-123\"}}", "/client/v4/zones/0123456789abcdef0123456789abcdef/dns_records")]
    [InlineData("DigitalOcean", "{\"domain_record\":{\"id\":123}}", "/v2/domains/example.com/records")]
    public async Task ProvidersCreateTxtAndDeleteOnlyReturnedId(string provider, string createResponse, string expectedPath)
    {
        var c = Config(); c.Provider = provider; c.CloudflareZoneId = provider == "Cloudflare" ? "0123456789abcdef0123456789abcdef" : "";
        var calls = 0;
        using var handler = new Handler(async r =>
        {
            Assert.Equal("Bearer test-secret", r.Headers.Authorization!.ToString());
            if (calls++ == 0)
            {
                Assert.Equal(HttpMethod.Post, r.Method); Assert.Equal(expectedPath, r.RequestUri!.AbsolutePath);
                using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
                Assert.Equal("TXT", json.RootElement.GetProperty("type").GetString());
                Assert.Equal(provider == "Cloudflare" ? "_acme-challenge.jellyfin.example.com" : "_acme-challenge.jellyfin", json.RootElement.GetProperty("name").GetString());
                Assert.Equal("verification-value", json.RootElement.GetProperty(provider == "Cloudflare" ? "content" : "data").GetString());
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(createResponse) };
            }
            Assert.Equal(HttpMethod.Delete, r.Method);
            Assert.Equal(expectedPath + (provider == "DigitalOcean" ? "/123" : "/created-123"), r.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var dns = new DnsProvider(c, "test-secret", handler);
        var lease = await dns.Create("verification-value", default); await dns.Delete(lease, default); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task ApiErrorsNeverExposeProviderResponseOrToken()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("sensitive-token-value") }));
        using var dns = new DnsProvider(Config(), "sensitive-token-value", handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => dns.Test(default));
        Assert.Contains("403", error.Message); Assert.DoesNotContain("sensitive-token-value", error.Message);
    }
    [Fact]
    public async Task RefusesCleanupWithDifferentProviderOrZone()
    {
        using var handler = new Handler(_ => throw new Exception("No network request should be made"));
        using var dns = new DnsProvider(Config(), "secret", handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dns.Delete(new DnsLease("GoDaddy", "someone-else.com", "", "123"), default));
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task StagingNeverChangesNetworkWhileProductionInstallsAndCanRollback(bool staging)
    {
        var root = Path.Combine(Path.GetTempPath(), "autocert-test-" + Guid.NewGuid().ToString("N"));
        var paths = new Mock<IApplicationPaths>(); paths.SetupGet(x => x.DataPath).Returns(root); paths.SetupGet(x => x.PluginsPath).Returns(Path.Combine(root, "plugins")); paths.SetupGet(x => x.PluginConfigurationsPath).Returns(Path.Combine(root, "config"));
        var plugin = new Plugin(paths.Object, Mock.Of<IXmlSerializer>());
        var c = Config(); c.UseStaging = staging; plugin.UpdateConfiguration(c);
        var store = new SecureStore(paths.Object);
        var certPath = Path.Combine(store.Root, "test.pfx"); File.WriteAllBytes(certPath, Pfx(c.Domain, "password"));
        store.Write(staging ? "state-staging" : "state-production", new CertificateState { Domain = c.Domain, Path = certPath, Password = "password" });
        var current = new NetworkConfiguration { CertificatePath = "previous.pfx", CertificatePassword = "old-password", EnableHttps = false, InternalHttpPort = 1234, InternalHttpsPort = 5678, RequireHttps = false };
        var network = new Mock<IServerConfigurationManager>(); network.Setup(x => x.GetConfiguration("network")).Returns(() => current);
        network.Setup(x => x.SaveConfiguration("network", It.IsAny<object>())).Callback<string, object>((_, value) => current = (NetworkConfiguration)value);
        var host = new Mock<IServerApplicationHost>(); var system = new Mock<ISystemManager>();
        var manager = new CertificateManager(store, network.Object, host.Object, system.Object, NullLogger<CertificateManager>.Instance);
        await manager.Run(new Progress<double>(), default);
        if (staging)
        { network.Verify(x => x.SaveConfiguration(It.IsAny<string>(), It.IsAny<object>()), Times.Never); host.Verify(x => x.NotifyPendingRestart(), Times.Never); }
        else
        {
            Assert.Equal(certPath, current.CertificatePath); Assert.Equal("password", current.CertificatePassword); Assert.True(current.EnableHttps); Assert.Equal(1234, current.InternalHttpPort); Assert.Equal(5678, current.InternalHttpsPort); Assert.False(current.RequireHttps);
            await manager.Rollback(default); Assert.Equal("previous.pfx", current.CertificatePath); Assert.Equal("old-password", current.CertificatePassword); Assert.False(current.EnableHttps); Assert.False(plugin.Configuration.Enabled);
        }
        system.Verify(x => x.Restart(), Times.Never);
        store.Set("secret-check", "do-not-store-in-clear");
        Assert.DoesNotContain("do-not-store-in-clear", File.ReadAllText(Path.Combine(store.Root, "secret-check.protected")));
        Assert.Equal("do-not-store-in-clear", new SecureStore(paths.Object).Get("secret-check"));
        // Keep test directories under the OS temp directory for investigation; no server files are touched.
    }
}
