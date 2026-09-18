using Jellyfin.Plugin.AutoCert.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AutoCert;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) { Instance = this; }
    public static Plugin Instance { get; private set; } = null!;
    public override string Name => "AutoCert";
    public override string Description => "Let's Encrypt certificates with automatic PFX installation and DNS verification.";
    public override Guid Id => Guid.Parse("6bb00c9d-9031-496b-85f3-e8262c547f35");
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "AutoCert", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" }];
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<SecureStore>();
        services.AddSingleton<CertificateManager>();
    }
}
