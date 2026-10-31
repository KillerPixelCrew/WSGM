using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>One running graphics publisher, as a page lists it.</summary>
/// <param name="Identity">The plugin instance.</param>
/// <param name="Name">The package's display name.</param>
/// <param name="ProfileKey">The key its values are stored under, <c>gpu:</c> and the plugin id.</param>
/// <param name="Health">The plugin's last reported health.</param>
/// <param name="HealthDetail">The plugin's detail for that health, or null.</param>
internal sealed record GpuPublisherView(
    PluginInstanceIdentity Identity,
    string Name,
    string ProfileKey,
    PluginHealth Health,
    string? HealthDetail);
