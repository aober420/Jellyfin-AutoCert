using Jellyfin.Plugin.AutoCert;
using MediaBrowser.Common.Configuration;
using Moq;
using Xunit;

public class CertificateRetentionTests
{
    [Fact]
    public void DeletesOnlyRetiredManagedFilesAfterFiveDaysAndExpiresRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), "autocert-retention-" + Guid.NewGuid().ToString("N"));
        var paths = new Mock<IApplicationPaths>(); paths.SetupGet(x => x.DataPath).Returns(root);
        var store = new SecureStore(paths.Object);
        string Create(string name) { var path = Path.Combine(store.Root, name); File.WriteAllText(path, "test fixture"); return path; }
        var old = Create("certificate-" + Guid.NewGuid().ToString("N") + ".pfx");
        var active = Create("certificate-" + Guid.NewGuid().ToString("N") + ".pfx");
        var staging = Create("staging-" + Guid.NewGuid().ToString("N") + ".pfx");
        var unmanaged = Create("my-certificate.pfx");
        var malformed = Create("certificate-not-a-guid.pfx");
        store.Write("previous", new PreviousCertificate(old, "password", true));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddYears(-1));
        var now = DateTimeOffset.UtcNow;
        CertificateRetention.Cleanup(store, new[] { active, staging }, now);
        Assert.True(File.Exists(old));
        CertificateRetention.Cleanup(store, new[] { active, staging }, now.AddDays(5).AddSeconds(-1));
        Assert.True(File.Exists(old)); Assert.NotNull(store.Read<PreviousCertificate>("previous"));
        CertificateRetention.Cleanup(store, new[] { active, staging }, now.AddDays(5));
        Assert.False(File.Exists(old)); Assert.Null(store.Read<PreviousCertificate>("previous"));
        foreach (var path in new[] { active, staging, unmanaged, malformed }) Assert.True(File.Exists(path));
    }

    [Fact]
    public void ReusingCertificateResetsItsRetentionClock()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(x => x.DataPath).Returns(Path.Combine(Path.GetTempPath(), "autocert-retention-" + Guid.NewGuid().ToString("N")));
        var store = new SecureStore(paths.Object);
        var path = Path.Combine(store.Root, "certificate-" + Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllText(path, "test fixture");
        var now = DateTimeOffset.UtcNow;
        CertificateRetention.Cleanup(store, Array.Empty<string>(), now);
        CertificateRetention.Cleanup(store, new[] { path }, now.AddDays(4));
        CertificateRetention.Cleanup(store, Array.Empty<string>(), now.AddDays(6));
        Assert.True(File.Exists(path));
        CertificateRetention.Cleanup(store, Array.Empty<string>(), now.AddDays(11));
        Assert.False(File.Exists(path));
    }
}
