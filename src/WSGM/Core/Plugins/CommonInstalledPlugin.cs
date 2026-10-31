using CommonManifest = WSGM.Plugin.Sdk.PluginManifest;

namespace WSGM.Core;

/// <summary>One metadata-validated independent plugin package.</summary>
internal sealed record CommonInstalledPlugin(string PackagePath, CommonManifest Manifest)
{
    public string Sha256 { get; init; } = "";
}
