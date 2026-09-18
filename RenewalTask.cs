using MediaBrowser.Model.Tasks;
namespace Jellyfin.Plugin.AutoCert;
public sealed class RenewalTask(CertificateManager manager) : IScheduledTask
{
    public string Name => "Renew HTTPS certificate";
    public string Key => "AutoCertRenewal";
    public string Description => "Check AutoCert's certificate and renew it when due. Disabled until configured.";
    public string Category => "AutoCert";
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => manager.Run(progress, cancellationToken);
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(12).Ticks }];
}
