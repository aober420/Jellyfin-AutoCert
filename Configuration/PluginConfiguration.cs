using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoCert.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string Domain { get; set; } = "";
    public string Email { get; set; } = "";
    public string Provider { get; set; } = "GoDaddy";
    public string GoDaddyAuthMode { get; set; } = "PAT";
    public string Zone { get; set; } = "";
    public string CloudflareZoneId { get; set; } = "";
    public bool UseStaging { get; set; } = true;
    public bool AcceptTerms { get; set; }
    public bool RotatePassword { get; set; } = true;
    public bool AutoRestart { get; set; }
    public int RenewBeforeDays { get; set; } = 30;
    public int PropagationSeconds { get; set; } = 600;
}
