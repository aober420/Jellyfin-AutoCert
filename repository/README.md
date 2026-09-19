# AutoCert plugin catalog

Add this URL under Jellyfin Dashboard > Plugins > Repositories:

```text
https://raw.githubusercontent.com/aober420/Jellyfin-AutoCert/main/repository/manifest.json
```

Open Catalog, install AutoCert, restart Jellyfin, and configure the plugin under My Plugins.

The manifest references the flat `AutoCert_1.0.0.0.zip` release asset. DLLs must be at the ZIP root because Jellyfin creates the destination plugin folder. Do not substitute the manual-install ZIP, which has an enclosing directory.

Jellyfin 12.1 requires the ZIP's **MD5** in the manifest `checksum` field. The release also includes a separate SHA-256 file for independent download verification. When publishing a new version, upload its package first, then update the manifest with that package's version, URL, target ABI, checksum, and timestamp. Retain previous version entries for compatible clients.
