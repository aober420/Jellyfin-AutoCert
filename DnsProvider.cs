using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.AutoCert.Configuration;

namespace Jellyfin.Plugin.AutoCert;

public sealed record DnsLease(string Provider, string Zone, string ZoneId, string RecordId, string? RecordValue = null, string? RecordName = null);

// Modern APIs delete exact record IDs. GoDaddy classic removes only the matching
// challenge value from the current TXT record set, preserving other returned values.
public sealed class DnsProvider : IDisposable
{
    private readonly HttpClient _http;
    private readonly PluginConfiguration _c;
    private bool Classic => _c.Provider == "GoDaddy" && _c.GoDaddyAuthMode == "Classic";
    public DnsProvider(PluginConfiguration config, string token, HttpMessageHandler? handler = null)
    {
        _c = config;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(Classic ? "sso-key" : "Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-AutoCert/0.2");
        _http.BaseAddress = new Uri(config.Provider switch
        {
            "GoDaddy" => Classic ? "https://api.godaddy.com/v1/domains/" : "https://api.godaddy.com/v3/domains/zones/",
            "Cloudflare" => "https://api.cloudflare.com/client/v4/zones/",
            "DigitalOcean" => "https://api.digitalocean.com/v2/domains/",
            _ => throw new InvalidOperationException("Unsupported DNS provider.")
        });
    }
    private string Records => _c.Provider switch
    {
        "GoDaddy" => Uri.EscapeDataString(_c.Zone) + (Classic ? "/records" : "/dns-records"),
        "Cloudflare" => Uri.EscapeDataString(_c.CloudflareZoneId) + "/dns_records",
        _ => Uri.EscapeDataString(_c.Zone) + "/records"
    };

    private async Task<JsonElement> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct);
        if ((method == HttpMethod.Delete || (Classic && method == HttpMethod.Get && path.Contains("/records/TXT/"))) && response.StatusCode == HttpStatusCode.NotFound) return default;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{_c.Provider} returned HTTP {(int)response.StatusCode}. Check the credentials, DNS permissions, zone, account eligibility, and provider rate limits.");
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        var content = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(content)) return default;
        using var json = JsonDocument.Parse(content);
        if (_c.Provider == "Cloudflare" && json.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean()) throw new InvalidOperationException("Cloudflare rejected the DNS request.");
        return json.RootElement.Clone();
    }

    public async Task Test(CancellationToken ct)
    {
        // This tests access without modifying DNS. Write access is exercised by a staging issuance.
        await Send(HttpMethod.Get, Records + (Classic ? "?limit=1" : _c.Provider == "GoDaddy" ? "?pageSize=1" : "?per_page=1"), null, ct);
        if (_c.Provider == "Cloudflare")
        {
            var zone = await Send(HttpMethod.Get, Uri.EscapeDataString(_c.CloudflareZoneId), null, ct);
            if (!string.Equals(zone.GetProperty("result").GetProperty("name").GetString(), _c.Zone, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cloudflare zone ID does not match the DNS zone name.");
        }
    }

    public async Task<DnsLease> Create(string value, CancellationToken ct)
    {
        var relative = Validation.RelativeName(_c.Domain, _c.Zone);
        object body = _c.Provider == "Cloudflare"
            ? new { type = "TXT", name = relative + "." + _c.Zone, content = value, ttl = 120 }
            : new { type = "TXT", name = relative, data = value, ttl = 600 };
        if (Classic)
        {
            await Send(HttpMethod.Patch, Records, new[] { body }, ct);
            return new DnsLease(_c.Provider, _c.Zone, _c.CloudflareZoneId, "", value, relative);
        }
        var result = await Send(HttpMethod.Post, Records, body, ct);
        var id = _c.Provider switch
        {
            "GoDaddy" => result.GetProperty("recordId").ToString(),
            "Cloudflare" => result.GetProperty("result").GetProperty("id").ToString(),
            _ => result.GetProperty("domain_record").GetProperty("id").ToString()
        };
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("DNS provider did not return a record ID. Inspect the challenge TXT record before retrying.");
        return new DnsLease(_c.Provider, _c.Zone, _c.CloudflareZoneId, id);
    }

    public async Task Delete(DnsLease lease, CancellationToken ct)
    {
        if (lease.Provider != _c.Provider || lease.Zone != _c.Zone || lease.ZoneId != _c.CloudflareZoneId) throw new InvalidOperationException("Pending DNS cleanup belongs to different provider settings. Restore those settings to clean up first.");
        if (Classic)
        {
            if (string.IsNullOrEmpty(lease.RecordValue) || lease.RecordName != Validation.RelativeName(_c.Domain, _c.Zone)) throw new InvalidOperationException("Classic DNS cleanup is missing the matching challenge name and value.");
            var path = Records + "/TXT/" + Uri.EscapeDataString(lease.RecordName);
            var records = new List<JsonElement>();
            const int pageSize = 100;
            for (var offset = 0; ; offset += pageSize)
            {
                if (offset >= 10000) throw new InvalidOperationException("Too many challenge records to clean up safely.");
                var page = await Send(HttpMethod.Get, path + "?limit=" + pageSize + "&offset=" + offset, null, ct);
                if (page.ValueKind == JsonValueKind.Undefined) break;
                if (page.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Unexpected DNS response; cleanup stopped without changing records.");
                var items = page.EnumerateArray().ToArray();
                if (items.Any(r => r.GetProperty("type").GetString() != "TXT" || r.GetProperty("name").GetString() != lease.RecordName)) throw new InvalidOperationException("DNS returned unrelated records; cleanup stopped without changing records.");
                records.AddRange(items);
                if (items.Length < pageSize) break;
            }
            var remaining = records.Where(r => r.GetProperty("data").GetString() != lease.RecordValue).ToArray();
            if (remaining.Length == records.Count) return;
            // Classic v1 has no record IDs or conditional writes. Do not run concurrent
            // ACME clients against the same challenge name; see the README.
            await Send(remaining.Length == 0 ? HttpMethod.Delete : HttpMethod.Put, path, remaining.Length == 0 ? null : remaining, ct);
            return;
        }
        if (lease.RecordValue != null) throw new InvalidOperationException("Classic DNS cleanup requires the classic authentication mode.");
        await Send(HttpMethod.Delete, Records + "/" + Uri.EscapeDataString(lease.RecordId), null, ct);
    }
    public void Dispose() => _http.Dispose();
}
