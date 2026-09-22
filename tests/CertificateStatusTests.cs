using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Jellyfin.Plugin.AutoCert;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

public class CertificateStatusTests
{
    [Theory]
    [InlineData("active", true)]
    [InlineData("pending", false)]
    [InlineData("http", false)]
    [InlineData("different", false)]
    [InlineData("password", false)]
    [InlineData("missing", false)]
    [InlineData("expired", false)]
    [InlineData("expired-disabled", false)]
    [InlineData("staging", false)]
    public async Task StatusReflectsRestartAndCertificateSettings(string scenario, bool active)
    {
        var root = Path.Combine(Path.GetTempPath(), "autocert-status-" + Guid.NewGuid().ToString("N"));
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(x => x.DataPath).Returns(root);
        paths.SetupGet(x => x.PluginsPath).Returns(Path.Combine(root, "plugins"));
        paths.SetupGet(x => x.PluginConfigurationsPath).Returns(Path.Combine(root, "config"));
        var plugin = new Plugin(paths.Object, Mock.Of<IXmlSerializer>());
        var config = BehaviorTests.Config(); config.UseStaging = scenario == "staging"; config.Enabled = scenario != "expired-disabled";
        plugin.UpdateConfiguration(config);
        var store = new SecureStore(paths.Object);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + config.Domain, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(config.Domain);
        request.CertificateExtensions.Add(names.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(scenario.StartsWith("expired") ? -1 : 30));
        var path = Path.Combine(store.Root, "certificate.pfx");
        if (scenario != "missing") File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, "password"));
        const string installed = "Certificate installed in Jellyfin settings. Restart Jellyfin to load it.";
        var state = new CertificateState { Domain = config.Domain, Path = path, Password = "password", RestartPending = true, Message = installed, NotAfter = cert.NotAfter.ToUniversalTime() };
        var stateKey = config.UseStaging ? "state-staging" : "state-production";
        store.Write(stateKey, state);
        var network = new Mock<IServerConfigurationManager>();
        network.Setup(x => x.GetConfiguration("network")).Returns(new NetworkConfiguration { EnableHttps = true, CertificatePath = scenario == "different" ? "other.pfx" : path, CertificatePassword = scenario == "password" ? "other" : "password" });
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(x => x.ListenWithHttps).Returns(scenario != "http");
        host.SetupGet(x => x.HasPendingRestart).Returns(scenario == "pending");
        var manager = new CertificateManager(store, network.Object, host.Object, Mock.Of<ISystemManager>(), NullLogger<CertificateManager>.Instance);
        var status = JsonSerializer.SerializeToElement(manager.Status());
        Assert.Equal(active ? "Certificate Active" : scenario.StartsWith("expired") ? "Certificate expired" : installed, status.GetProperty("message").GetString());
        Assert.Equal(active ? "Certificate Active" : scenario.StartsWith("expired") ? "Certificate expired" : "Idle", status.GetProperty("activity").GetString());
        // A successful restart must not erase a later renewal failure.
        state.Message = "Renewal failed"; store.Write(stateKey, state);
        Assert.Equal("Renewal failed", JsonSerializer.SerializeToElement(manager.Status()).GetProperty("message").GetString());
        // Status polling never changes certificate configuration or stored issuance state.
        network.Verify(x => x.SaveConfiguration(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
        Assert.True(store.Read<CertificateState>(stateKey)!.RestartPending);
        var retired = Path.Combine(store.Root, "certificate-" + Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllText(retired, "old fixture");
        store.Write("retired-certificates", new Dictionary<string, DateTimeOffset> { [retired] = DateTimeOffset.UtcNow.AddDays(-6) });
        config.Enabled = false; plugin.UpdateConfiguration(config);
        await manager.Run(new Progress<double>(), default);
        Assert.Equal(!active, File.Exists(retired));
        // Forced issuance bypasses both disabled automation and a current certificate,
        // but still stops at the cooldown before touching the DNS provider.
        state.LastAttempt = DateTimeOffset.UtcNow;
        store.Write(stateKey, state);
        var cooldown = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Run(new Progress<double>(), default, force: true));
        Assert.Contains("six hours", cooldown.Message);

    }
}
