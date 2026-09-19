namespace Jellyfin.Plugin.AutoCert;

public static class CertificateRetention
{
    // Start retention when an unused file is first observed, never from its creation date.
    public static void Cleanup(SecureStore store, IEnumerable<string> protectedPaths, DateTimeOffset now)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var keep = new HashSet<string>(protectedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath), comparer);
        if ((File.GetAttributes(store.Root) & FileAttributes.ReparsePoint) != 0) return;
        var retired = store.Read<Dictionary<string, DateTimeOffset>>("retired-certificates") ?? new();
        var next = new Dictionary<string, DateTimeOffset>(comparer);
        foreach (var path in Directory.EnumerateFiles(store.Root, "*.pfx", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var id = name.StartsWith("certificate-", StringComparison.Ordinal) ? name[12..] : name.StartsWith("staging-", StringComparison.Ordinal) ? name[8..] : "";
            if (!Guid.TryParseExact(id, "N", out _) || keep.Contains(Path.GetFullPath(path)) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            var since = retired.TryGetValue(path, out var timestamp) ? timestamp : now;
            next[path] = since;
            if (now - since < TimeSpan.FromDays(5)) continue;
            try
            {
                // Expire the rollback entry before removing its file.
                var previous = store.Read<PreviousCertificate>("previous");
                if (previous != null && !string.IsNullOrEmpty(previous.Path) && comparer.Equals(Path.GetFullPath(previous.Path), Path.GetFullPath(path)))
                    store.Set("previous", "null");
                File.Delete(path);
                next.Remove(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retry on the next scheduled check if another process has the file open.
            }
        }
        store.Write("retired-certificates", next);
    }
}
