using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Certes;
using Certes.Acme.Resource;
using Certes.Acme;
using Jellyfin.Plugin.AutoCert.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCert;

public sealed class CertificateState
{
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "";
    public string Password { get; set; } = "";
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public string Message { get; set; } = "Not yet issued.";
    public bool RestartPending { get; set; }
}
public sealed record PreviousCertificate(string Path, string Password, bool EnableHttps);
public sealed record PendingDns(PluginConfiguration Config, string Token, DnsLease Lease);

public sealed class CertificateManager
{
    private readonly SecureStore _store;
    private readonly IServerConfigurationManager _network;
    private readonly IServerApplicationHost _host;
    private readonly ISystemManager _system;
    private readonly ILogger<CertificateManager> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string _activity = "Idle";
    public CertificateManager(SecureStore store, IServerConfigurationManager network, IServerApplicationHost host, ISystemManager system, ILogger<CertificateManager> log)
    { _store = store; _network = network; _host = host; _system = system; _log = log; }

    private const string InstalledMessage = "Certificate installed in Jellyfin settings. Restart Jellyfin to load it.";

    public object Status()
    {
        var c = Plugin.Instance.Configuration;
        var state = _store.Read<CertificateState>(StateKey(c)) ?? new();
        var expired = state.NotAfter != default && state.NotAfter <= DateTimeOffset.UtcNow;
        var active = !expired && !c.UseStaging && IsCertificateActive(state, c.Domain);
        var message = active && state.Message == InstalledMessage ? "Certificate Active" : state.Message;
        var activity = active && _activity is "Idle" or "Complete" or "Certificate is current" ? "Certificate Active" : _activity;
        if (expired)
        {
            if (_activity is "Idle" or "Complete" or "Certificate is current" or "Disabled") activity = "Certificate expired";
            // Preserve errors and progress, but replace the obsolete restart instruction.
            message = state.Message == InstalledMessage ? "Certificate expired" : state.Message;
            if (activity != "Certificate expired" && message != "Certificate expired") message = "Certificate expired. " + message;
        }
        return new { activity, domain = state.Domain, issued = state.NotBefore == default ? (DateTimeOffset?)null : state.NotBefore, expires = state.NotAfter == default ? (DateTimeOffset?)null : state.NotAfter, lastAttempt = state.LastAttempt, message, path = state.Path, hasToken = !string.IsNullOrEmpty(_store.Get("token-" + SafeProvider(c.Provider))), hasClassicCredentials = !string.IsNullOrEmpty(_store.Get("godaddy-classic")), hasPassword = !string.IsNullOrEmpty(_store.Get("fixed-password")), pendingDnsCleanup = _store.Read<PendingDns>("pending-dns") != null, restartRequired = _host.HasPendingRestart, hasBackup = _store.Read<PreviousCertificate>("previous") != null };
    }
    // Derive display status without rewriting issuance state or hiding renewal failures.
    private bool IsCertificateActive(CertificateState state, string domain)
    {
        if (_host.HasPendingRestart || !_host.ListenWithHttps || state.Domain != domain || string.IsNullOrEmpty(state.Path)) return false;
        var current = _network.GetConfiguration<NetworkConfiguration>("network");
        if (!current.EnableHttps || current.CertificatePath != state.Path || current.CertificatePassword != state.Password) return false;
        try
        {
            using var certificate = Validation.CheckPfx(File.ReadAllBytes(state.Path), state.Password, domain);
            return certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }
    private static string SafeProvider(string provider) => Validation.Providers.Contains(provider) ? provider : throw new InvalidOperationException("Unsupported DNS provider.");
    private static string StateKey(PluginConfiguration c) => c.UseStaging ? "state-staging" : "state-production";
    private static PluginConfiguration Clone(PluginConfiguration c) => JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(c))!;

    public async Task Save(PluginConfiguration c, string? token, string? password, CancellationToken ct, string? apiKey = null, string? apiSecret = null)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("Wait for the certificate operation to finish before changing settings.");
        try
        {
            Validation.Check(c);
            if (!string.IsNullOrEmpty(password) && password.Length < 12) throw new InvalidOperationException("Use a PFX password of at least 12 characters.");
            if (!string.IsNullOrWhiteSpace(apiKey) || !string.IsNullOrWhiteSpace(apiSecret))
            {
                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret)) throw new InvalidOperationException("Enter both the GoDaddy API key and secret together, or leave both blank to keep the saved pair.");
                if (apiKey.Any(char.IsWhiteSpace) || apiSecret.Any(char.IsWhiteSpace) || apiKey.Contains(':') || apiSecret.Contains(':')) throw new InvalidOperationException("The API key and secret must not contain spaces or colons.");
                _store.Set("godaddy-classic", apiKey + ":" + apiSecret);
            }
            if (!string.IsNullOrWhiteSpace(token)) _store.Set("token-" + c.Provider, token.Trim());
            if (!string.IsNullOrEmpty(password)) _store.Set("fixed-password", password);
            Plugin.Instance.UpdateConfiguration(c);
        }
        finally { _gate.Release(); }
    }
    public async Task Test(CancellationToken ct)
    {
        var c = Clone(Plugin.Instance.Configuration);
        Validation.Check(c);
        using var dns = new DnsProvider(c, GetToken(c));
        await dns.Test(ct);
    }
    private string GetToken(PluginConfiguration c) => c.Provider == "GoDaddy" && c.GoDaddyAuthMode == "Classic"
        ? _store.Get("godaddy-classic") ?? throw new InvalidOperationException("Save your GoDaddy production API key and secret first.")
        : _store.Get("token-" + SafeProvider(c.Provider)) ?? throw new InvalidOperationException("Save an API token for this DNS provider first.");

    public async Task Run(IProgress<double> progress, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        CertificateState? state = null;
        PluginConfiguration? c = null;
        try
        {
            c = Clone(Plugin.Instance.Configuration);
            var installed = _store.Read<CertificateState>("state-production");
            if (installed != null && IsCertificateActive(installed, installed.Domain))
            {
                var staging = _store.Read<CertificateState>("state-staging");
                try { CertificateRetention.Cleanup(_store, new[] { installed.Path, staging?.Path ?? "" }, DateTimeOffset.UtcNow); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _log.LogWarning("AutoCert could not clean up old certificates; it will retry on the next check."); }
            }
            if (!c.Enabled) { _activity = "Disabled"; return; }
            Validation.Check(c);
            if (!c.AcceptTerms) throw new InvalidOperationException("Accept the Let's Encrypt subscriber agreement in settings before issuing.");
            _activity = "Cleaning up any previous verification record";
            await Cleanup();
            state = _store.Read<CertificateState>(StateKey(c)) ?? new();
            if (state.Domain == c.Domain && File.Exists(state.Path))
            {
                using var existing = X509CertificateLoader.LoadPkcs12FromFile(state.Path, state.Password, X509KeyStorageFlags.EphemeralKeySet);
                if (existing.HasPrivateKey && existing.MatchesHostname(c.Domain, false, false) && !Validation.Due(DateTimeOffset.UtcNow, existing.NotBefore.ToUniversalTime(), existing.NotAfter.ToUniversalTime(), c.RenewBeforeDays))
                {
                    if (!c.UseStaging)
                    {
                        Apply(state);
                        if (state.RestartPending && !_host.HasPendingRestart) { state.RestartPending = false; _store.Write(StateKey(c), state); }
                        if (state.RestartPending && c.AutoRestart) _system.Restart();
                    }
                    _activity = "Certificate is current";
                    progress.Report(100);
                    return;
                }
            }
            if (state.LastAttempt is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(6))
                throw new InvalidOperationException("The next issuance attempt is allowed six hours after the last attempt. This protects against repeated failed orders and rate limits.");
            var token = GetToken(c);
            using var dns = new DnsProvider(c, token);
            _activity = "Checking DNS access";
            await dns.Test(ct);
            state.LastAttempt = DateTimeOffset.UtcNow;
            state.Message = "Issuance started.";
            _store.Write(StateKey(c), state);
            progress.Report(10);
            var accountName = c.UseStaging ? "account-staging" : "account-production";
            var pem = _store.Get(accountName);
            var accountKey = pem == null ? KeyFactory.NewKey(KeyAlgorithm.ES256) : KeyFactory.FromPem(pem);
            if (pem == null) _store.Set(accountName, accountKey.ToPem());
            var acme = new AcmeContext(c.UseStaging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2, accountKey);
            await acme.NewAccount(c.Email, true);
            ct.ThrowIfCancellationRequested();
            var order = await acme.NewOrder([c.Domain]);
            foreach (var auth in await order.Authorizations())
            {
                if ((await auth.Resource()).Status == AuthorizationStatus.Valid) continue;
                var challenge = await auth.Dns();
                ct.ThrowIfCancellationRequested();
                var lease = await dns.Create(acme.AccountKey.DnsTxt(challenge.Token), ct);
                try
                {
                    _store.Write("pending-dns", new PendingDns(c, token, lease));
                    _activity = $"Waiting {c.PropagationSeconds} seconds for DNS propagation";
                    progress.Report(25);
                    await Task.Delay(TimeSpan.FromSeconds(c.PropagationSeconds), ct);
                    _activity = "Let's Encrypt is verifying DNS";
                    await challenge.Validate();
                    var valid = false;
                    for (var attempt = 0; attempt < 60; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var resource = await auth.Resource();
                        if (resource.Status == AuthorizationStatus.Valid) { valid = true; break; }
                        if (resource.Status != AuthorizationStatus.Pending) throw new InvalidOperationException("Let's Encrypt rejected DNS verification. Check authoritative DNS, CAA records, delegation, and the DNS wait time.");
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    }
                    if (!valid) throw new InvalidOperationException("DNS verification timed out. Increase the DNS wait time and try again later.");
                }
                finally
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await dns.Delete(lease, cleanupTimeout.Token);
                    _store.Set("pending-dns", "null");
                }
            }
            _activity = "Generating and verifying the PFX";
            progress.Report(70);
            ct.ThrowIfCancellationRequested();
            var privateKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
            var chain = await order.Generate(new CsrInfo { CommonName = c.Domain }, privateKey);
            var password = c.RotatePassword ? NewPassword() : _store.Get("fixed-password") ?? NewPassword();
            if (!c.RotatePassword && _store.Get("fixed-password") == null) _store.Set("fixed-password", password);
            var pfx = chain.ToPfx(privateKey).Build("Jellyfin AutoCert", password);
            using var verified = Validation.CheckPfx(pfx, password, c.Domain);
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(_store.Root, (c.UseStaging ? "staging-" : "certificate-") + Guid.NewGuid().ToString("N") + ".pfx");
            SecureStore.AtomicWrite(path, pfx);
            using var diskVerified = Validation.CheckPfx(File.ReadAllBytes(path), password, c.Domain);
            state.Domain = c.Domain;
            state.Path = path;
            state.Password = password;
            state.NotBefore = verified.NotBefore.ToUniversalTime();
            state.NotAfter = verified.NotAfter.ToUniversalTime();
            state.Message = c.UseStaging ? "Staging issuance succeeded. Test certificate was NOT installed. Switch off staging and run again for a trusted certificate." : "Certificate generated; applying to Jellyfin.";
            _store.Write(StateKey(c), state);
            if (!c.UseStaging)
            {
                Apply(state);
                state.Message = InstalledMessage;
                _store.Write(StateKey(c), state);
            }
            progress.Report(100);
            _activity = "Complete";
            _log.LogInformation("AutoCert completed {Environment} issuance for {Domain}; expires {Expiration}", c.UseStaging ? "staging" : "production", c.Domain, state.NotAfter);
            if (!c.UseStaging && c.AutoRestart) { _activity = "Restart requested"; _system.Restart(); }
        }
        catch (Exception ex)
        {
            var message = SafeMessage(ex);
            _activity = message;
            if (state != null && c != null) { state.Message = message; _store.Write(StateKey(c), state); }
            _log.LogWarning("AutoCert: {Message}", message);
            throw new InvalidOperationException(message);
        }
        finally { _gate.Release(); }
    }
    private async Task Cleanup()
    {
        var pending = _store.Read<PendingDns>("pending-dns");
        if (pending == null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var dns = new DnsProvider(pending.Config, pending.Token);
        await dns.Delete(pending.Lease, timeout.Token);
        _store.Set("pending-dns", "null");
    }
    private void Apply(CertificateState state)
    {
        var current = _network.GetConfiguration<NetworkConfiguration>("network");
        if (current.CertificatePath == state.Path && current.CertificatePassword == state.Password && current.EnableHttps) return;
        var replacement = JsonSerializer.Deserialize<NetworkConfiguration>(JsonSerializer.Serialize(current))!;
        _store.Write("previous", new PreviousCertificate(current.CertificatePath, current.CertificatePassword, current.EnableHttps));
        replacement.CertificatePath = state.Path;
        replacement.CertificatePassword = state.Password;
        replacement.EnableHttps = true;
        _network.SaveConfiguration("network", replacement);
        _host.NotifyPendingRestart();
        state.RestartPending = true;
        _store.Write("state-production", state);
    }
    public async Task Rollback(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("Wait for the certificate operation to finish before restoring.");
        try
        {
            var previous = _store.Read<PreviousCertificate>("previous") ?? throw new InvalidOperationException("There is no previous certificate configuration.");
            if (previous.EnableHttps)
            {
                using var cert = X509CertificateLoader.LoadPkcs12FromFile(previous.Path, previous.Password, X509KeyStorageFlags.EphemeralKeySet);
                if (!cert.HasPrivateKey) throw new InvalidOperationException("Previous certificate has no private key.");
            }
            var current = _network.GetConfiguration<NetworkConfiguration>("network");
            var replacement = JsonSerializer.Deserialize<NetworkConfiguration>(JsonSerializer.Serialize(current))!;
            replacement.CertificatePath = previous.Path;
            replacement.CertificatePassword = previous.Password;
            replacement.EnableHttps = previous.EnableHttps;
            _network.SaveConfiguration("network", replacement);
            var config = Clone(Plugin.Instance.Configuration);
            config.Enabled = false;
            Plugin.Instance.UpdateConfiguration(config);
            _host.NotifyPendingRestart();
            _activity = "Previous settings restored; AutoCert disabled. Restart Jellyfin to apply.";
        }
        finally { _gate.Release(); }
    }
    private static string NewPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static string SafeMessage(Exception ex) => ex switch
    {
        OperationCanceledException => "Operation canceled or timed out. Check certificate and DNS cleanup status.",
        InvalidOperationException when ex.Source == typeof(CertificateManager).Assembly.GetName().Name => ex.Message,
        _ => "Certificate operation failed (" + ex.GetType().Name + "). Check DNS access, permissions, connectivity, and CA limits. Sensitive details were omitted."
    };
}

