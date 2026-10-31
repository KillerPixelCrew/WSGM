using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Installed instance and its current admission outcome, including failures retained for presentation.</summary>
/// <param name="Identity">Configured or implicitly enabled package/instance key.</param>
/// <param name="Manifest">Selected validated package metadata.</param>
/// <param name="Registration">Live registration, or null when no runtime has been admitted.</param>
/// <param name="Error">Load or runtime failure detail, or null when none is retained.</param>
/// <param name="SteamCefEnabled">Whether current user policy admits this instance's unrestricted frontend.</param>
internal sealed record CommonPluginInstanceView(
    PluginInstanceIdentity Identity,
    PluginManifest Manifest,
    PluginRegistration? Registration,
    string? Error,
    bool SteamCefEnabled = false);
