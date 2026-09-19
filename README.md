![AutoCert logo](assets/autocert-logo.png)

# AutoCert for Jellyfin 12.1

AutoCert obtains Let's Encrypt certificates using DNS verification, packages the private key and chain into a password-protected PFX, and updates Jellyfin's HTTPS settings. Each server owner supplies their own domain and DNS credentials through the plugin's configuration page.

**Version 0.2.1 — initial test release.** Built for Jellyfin **12.1.0 / .NET 10**. The release builds and passes automated tests and an isolated Jellyfin 12.1 startup/API smoke test. Live DNS issuance and automatic service restart have not been tested with provider accounts. Use staging before enabling production issuance.

## Providers

| Provider | Configuration | Token permissions |
|---|---|---|
| GoDaddy API key + secret | DNS zone, production API key, production secret | Classic Domains/DNS API access |
| GoDaddy PAT | DNS zone and production v3 personal access token | `domains.domain:read`, `domains.dns:update` |
| Cloudflare | DNS zone, zone ID, and API token | Zone DNS Edit and Zone Read, restricted to the selected zone |
| DigitalOcean | DNS zone and personal access token | Domain record read, create, and delete access |

Choose the **authoritative DNS host**, which can differ from the company that sold the domain. GoDaddy supports both classic API key/secret pairs and v3 personal access tokens. Existing settings retain PAT mode on upgrade; choose **API key and secret** to switch. Create classic credentials at https://classic-developer.godaddy.com/keys (choose Production). This release implements these three providers, not every registrar. It does not require opening public port 80.

## Install through Jellyfin

Requires Jellyfin 12.1 or later in the compatible 12.x series. This is an initial test release; use staging mode before issuing production certificates.

1. Open **Dashboard > Plugins > Repositories** and add a repository named **AutoCert**.
2. Click the copy button in the upper-right corner of this URL box, then paste it into Jellyfin’s **Repository URL** field:

   ```text
   https://raw.githubusercontent.com/aober420/Jellyfin-AutoCert/main/repository/manifest.json
   ```

3. Save, open **Catalog**, select **AutoCert**, and install version **0.2.1.0**.
4. Restart Jellyfin, then open **My Plugins > AutoCert** to configure your domain and DNS credentials.

Release downloads are available at [GitHub Releases](https://github.com/aober420/Jellyfin-AutoCert/releases). `AutoCert_0.2.1.0.zip` is the catalog package: its DLLs are at the ZIP root because Jellyfin creates the plugin directory. `AutoCert-0.2.1-Jellyfin-12.1.zip` is the manual installation package described below.

## Manual installation on Windows

1. Extract the installation ZIP. It contains an `AutoCert_0.2.1.0` folder.
2. Find your actual Jellyfin data/plugins directory using the server's dashboard paths. A typical Windows service install uses `C:\ProgramData\Jellyfin\Server\plugins`, but portable/custom installations differ.
3. Stop Jellyfin and copy the entire `AutoCert_0.2.1.0` folder into that plugins directory, including its dependency DLLs. Do not copy the source ZIP or `.cs` files there.
4. Start Jellyfin. Open **Dashboard → Plugins → My Plugins → AutoCert**.

The public catalog uses the repository URL above. Installing or upgrading requires a Jellyfin restart. Live DNS issuance has not yet been validated with provider accounts; start with staging.

## First setup

For a hostname such as `jellyfin.example.com`, enter that hostname as the domain and `example.com` as the DNS zone. Enter an email address for the ACME account and the appropriate provider credentials. For GoDaddy classic, choose **API key and secret** and fill in both fields. Leave both blank on later saves to keep the saved pair; replacing only one field is rejected. Leave token fields blank on later saves to retain the stored token. Switching providers uses the separate token saved for that provider.

1. Keep **staging** enabled. Select **Enable automatic certificate management**, review and accept the Let's Encrypt subscriber agreement, and save.
2. Click **Test DNS access**. This is read-only; it confirms access, not create/delete permissions.
3. Click **Check / issue certificate**. DNS verification typically takes several minutes. Start with the default 600-second propagation wait for GoDaddy.
4. Confirm that status reports successful staging issuance. **The staging PFX is never installed into Jellyfin**, because clients would not trust it.
5. Turn off staging, save, and run the check again to obtain and install a production certificate. Staging and production use separate account keys and certificate state.
6. Restart Jellyfin to load the certificate, or enable automatic restart after confirming that Jellyfin's own restart command works in your service/container setup.

New certificates are generated when missing, when the configured hostname changes, or when renewal is due. The check runs every 12 hours and renews at the configured threshold, capped at one-third of the issued lifetime. It does not assume certificates always last 90 days. Issuance attempts have a six-hour cooldown per environment; pressing Check repeatedly does not bypass it.

Tokens can be created using your provider's developer dashboard. Do not enter your account login password as the API token. For GoDaddy, use **production** credentials even when Let's Encrypt is in staging; DNS verification still happens in your real public DNS zone.

## What gets changed

- A temporary `_acme-challenge` TXT record is created. PAT, Cloudflare, and DigitalOcean cleanup deletes the exact record ID returned by the provider. GoDaddy classic has no individual record IDs: cleanup reads the current TXT values at the challenge name, removes the matching challenge value, and writes the remaining values back (or deletes that name if none remain). Unrelated names are not modified. **Do not run concurrent certificate clients against the same challenge name in classic mode**: its API has no conditional write, so simultaneous changes between the read and write cannot be protected from a race.
- A new PFX is written to a unique file in `<Jellyfin data>/autocert`. The PFX is reopened and checked for a private key, the exact requested hostname, and validity before installation.
- Jellyfin's certificate path, certificate password, and HTTPS-enabled setting are updated through its configuration manager. Existing port settings and the HTTPS-required setting are preserved.
- Password rotation is on by default. Turning it off allows a saved or automatically generated fixed password. A changed fixed password takes effect on the next issuance.
- Previous certificate settings and previous PFX files are retained. **Restore previous settings** restores the previous path/password/HTTPS setting and disables AutoCert. Restart afterward. It cannot restore an old certificate file that was deleted externally.
- Auto-restart is off by default. Enabling it can interrupt playback. This uses Jellyfin's own restart mechanism; it does not install a Windows service or external restart helper.

## Credentials and recovery

DNS tokens, GoDaddy API key/secret pairs, ACME account keys, PFX passwords in plugin state, DNS cleanup state, and rollback settings are encrypted with ASP.NET Data Protection. The AutoCert directory is restricted to the service account, SYSTEM, and Administrators on Windows; Windows DPAPI protects the key ring for the current account. On other systems the directory is owner-only, but the data-protection key ring relies on filesystem permissions. Linux has not been smoke-tested in this release.

**Jellyfin itself stores the active certificate password in its network configuration.** The plugin cannot change that format. Protect Jellyfin's entire data/config directory and backups. Tokens are never returned by the settings/status endpoints or included in plugin log messages. Those endpoints require administrator privileges. Configure the plugin over your trusted local connection or existing HTTPS.

The plugin runs only while Jellyfin runs. Moving to a different Windows service account requires re-entering credentials because of DPAPI. Preserve the key ring and encrypted state together when backing up. A provider request with an uncertain response, or a process crash at the moment a record is created, can leave an unused challenge TXT record; inspect that record if provider cleanup is reported as pending. Recorded cleanup is retried on the next enabled run.

If HTTPS prevents access, use your existing local HTTP access, or stop Jellyfin and restore your saved network configuration. Keep a backup of the original network configuration before first production installation. Old PFX files are not automatically deleted in this release, so administrators can retain or remove older generations deliberately after verifying which certificate is active.

## Limitations

- One exact hostname per installation; no wildcard/multi-domain certificates.
- DNS-01 only. No HTTP-01 listener, custom scripts, CNAME challenge delegation, or unsupported DNS-provider adapters.
- Credentials must permit actual DNS operations for the selected zone. Account restrictions or expired tokens can prevent renewal.
- Email changes do not update an already-registered ACME account contact in this release; the initial registration email is used.
- Does not adopt an existing external certificate: the first production run issues a new managed certificate.
- No promise of uninterrupted HTTPS if Jellyfin is stopped, DNS is unavailable, or the configured restart method fails. Check the certificate status and scheduled-task result.
- Certes does not expose cancellation on every ACME request. Cancellation is checked between ACME operations and during DNS waits; an in-flight library request may finish first.

## Build and test

Install the .NET 10 SDK, then run from this directory:

```powershell
dotnet build .\Jellyfin.Plugin.AutoCert.csproj -c Release
dotnet test .\tests\AutoCert.Tests.csproj -c Release
```

The install folder needs `Jellyfin.Plugin.AutoCert.dll`, `Certes.dll`, `BouncyCastle.Crypto.dll`, and `Newtonsoft.Json.dll`. Do not ship Jellyfin's own framework assemblies; the running server provides those.

Automated tests cover hostname/zone validation, short-lifetime renewal timing, invalid PFX rejection, provider TXT creation and exact-ID cleanup, response redaction, wrong-zone cleanup rejection, encrypted state persistence, staging isolation, production configuration updates, and rollback. The isolated Jellyfin smoke test verified plugin discovery, page delivery, settings saves, secret flags, task execution, anonymous HTTP 401, and non-admin HTTP 403. No live DNS records or certificates were created during development.

## Extending provider support

`DnsProvider.cs` owns each provider's fixed HTTPS endpoint, authorization, TXT creation, and provider-specific cleanup. Add a provider there, expose its configuration fields, and extend the fake-HTTP tests. Avoid whole-zone replacement APIs. Preserve unrelated TXT values and use exact-ID cleanup where supported. GoDaddy classic requires a read/filter/write operation at the single challenge name. A provider with different authentication needs additional protected credential fields and corresponding UI rather than an arbitrary user-supplied API URL.

## References

- [Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template)
- [GoDaddy DNS API and PAT scopes](https://developer.godaddy.com/en/docs/api-users/domains/manage/dns)
- [Cloudflare DNS record API](https://developers.cloudflare.com/api/resources/dns/subresources/records/methods/create/)
- [DigitalOcean domain records API](https://docs.digitalocean.com/products/networking/dns/reference/api/domain-records/)
- [Let's Encrypt validation](https://letsencrypt.org/docs/challenge-types/)
- [Certes ACME library](https://github.com/fszlin/certes)

License: GPL-3.0-only for the plugin source. See `THIRD-PARTY-NOTICES.md` for bundled dependencies.

## Upgrade from 0.1.0

Stop Jellyfin. Move the old AutoCert plugin folder out of the plugins directory and keep it as a backup, then copy in `AutoCert_0.2.1.0` and start Jellyfin. Do not leave both plugin versions installed. Keep the existing plugin XML configuration and `<Jellyfin data>/autocert` directory. Select GoDaddy, choose **API key and secret**, enter both production values, save, and test DNS access. Use a staging issuance before relying on production renewal.

Version 0.2.0 adds six tests for classic authentication, TXT value preservation, already-absent records, and unexpected name rejection (26 passing cases total). An isolated Jellyfin 12.1 check also verified classic credential saving, rejection of a partial pair, settings redaction, and delivery of the new fields. No live DNS credentials were used. Classic keys remain subject to GoDaddy account/API eligibility and its legacy API lifecycle.



Version 0.2.1 fixes certificate status after restart: the page displays **Certificate Active** when HTTPS is enabled, Jellyfin reports HTTPS listening with no pending restart, and its certificate settings match a valid AutoCert PFX. Staging results and renewal errors remain visible. All 34 automated test cases pass.
