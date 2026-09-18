using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.AutoCert.Configuration;

namespace Jellyfin.Plugin.AutoCert;

public static class Validation
{
    public static readonly string[] Providers = ["GoDaddy", "Cloudflare", "DigitalOcean"];
    public static string Host(string value)
    {
        var host = new IdnMapping().GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant();
        if (host.Length > 253 || !host.Contains('.') || host.Split('.').Any(x => x.Length is 0 or > 63 || x[0] == '-' || x[^1] == '-' || x.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
            throw new InvalidOperationException("Enter a full domain name, without a URL, port, path, or wildcard.");
        return host;
    }

    public static void Check(PluginConfiguration c)
    {
        if (c.Provider == "GoDaddy" && c.GoDaddyAuthMode is not ("PAT" or "Classic")) throw new InvalidOperationException("Choose a GoDaddy authentication method.");
        c.Domain = Host(c.Domain);
        c.Zone = Host(c.Zone);
        if (c.Domain != c.Zone && !c.Domain.EndsWith("." + c.Zone, StringComparison.Ordinal)) throw new InvalidOperationException("The domain must belong to the DNS zone.");
        if (!MailAddress.TryCreate(c.Email, out var email) || email.Address != c.Email) throw new InvalidOperationException("Enter a valid email address.");
        if (!Providers.Contains(c.Provider)) throw new InvalidOperationException("Choose a supported DNS provider.");
        if (c.Provider == "Cloudflare" && (c.CloudflareZoneId.Length != 32 || !c.CloudflareZoneId.All(char.IsAsciiHexDigit))) throw new InvalidOperationException("Enter the 32-character Cloudflare zone ID.");
        if (c.PropagationSeconds is < 30 or > 3600) throw new InvalidOperationException("DNS wait time must be between 30 and 3600 seconds.");
        if (c.RenewBeforeDays is < 1 or > 30) throw new InvalidOperationException("Renewal threshold must be between 1 and 30 days.");
    }

    public static string RelativeName(string domain, string zone) => domain == zone ? "_acme-challenge" : "_acme-challenge." + domain[..^(zone.Length + 1)];
    public static bool Due(DateTimeOffset now, DateTimeOffset start, DateTimeOffset end, int days) => end - now <= TimeSpan.FromDays(Math.Min(days, (end - start).TotalDays / 3));

    public static X509Certificate2 CheckPfx(byte[] bytes, string password, string domain)
    {
        var cert = X509CertificateLoader.LoadPkcs12(bytes, password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        if (!cert.HasPrivateKey || !cert.MatchesHostname(domain, allowWildcards: false, allowCommonName: false) || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow || cert.NotBefore.ToUniversalTime() > DateTime.UtcNow.AddMinutes(5))
        {
            cert.Dispose();
            throw new InvalidOperationException("The generated PFX failed private-key, hostname, or validity verification.");
        }
        return cert;
    }
}
