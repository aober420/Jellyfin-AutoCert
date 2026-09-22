using MediaBrowser.Model.Tasks;
namespace Jellyfin.Plugin.AutoCert;

public sealed class ManualIssuanceTask(CertificateManager manager) : IScheduledTask
{
    public string Name => "Issue new HTTPS certificate now";
    public string Key => "AutoCertManualIssuance";
    public string Description => "Issue using saved settings regardless of the renewal window. The six-hour cooldown still applies.";
    public string Category => "AutoCert";
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => manager.Run(progress, cancellationToken, force: true);
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
