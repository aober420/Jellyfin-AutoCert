using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.AutoCert;

// No credentials are stored in the plugin's ordinary XML configuration or returned to the browser.
public sealed class SecureStore
{
    private readonly IDataProtector _protector;
    private readonly object _sync = new();
    public string Root { get; }

    public SecureStore(IApplicationPaths paths)
    {
        Root = Path.Combine(paths.DataPath, "autocert");
        RestrictDirectory(Root);
        var keys = Path.Combine(Root, "keys");
        RestrictDirectory(keys);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keys), b =>
        {
            b.SetApplicationName("Jellyfin.AutoCert.v1");
            if (OperatingSystem.IsWindows()) b.ProtectKeysWithDpapi();
        });
        _protector = provider.CreateProtector("private-data");
    }

    public string? Get(string name)
    {
        lock (_sync)
        {
            var path = Path.Combine(Root, name + ".protected");
            return File.Exists(path) ? _protector.Unprotect(File.ReadAllText(path)) : null;
        }
    }

    public void Set(string name, string value)
    {
        lock (_sync) { AtomicWrite(Path.Combine(Root, name + ".protected"), System.Text.Encoding.UTF8.GetBytes(_protector.Protect(value))); }
    }

    public T? Read<T>(string name) where T : class => Get(name) is { } text ? JsonSerializer.Deserialize<T>(text) : null;
    public void Write<T>(string name, T value) => Set(name, JsonSerializer.Serialize(value));

    public static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void RestrictDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var ids = new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
            foreach (var id in ids)
                security.AddAccessRule(new FileSystemAccessRule(id, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

