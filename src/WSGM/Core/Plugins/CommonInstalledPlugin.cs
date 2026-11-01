using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>One metadata-validated independent plugin package.</summary>
internal sealed record CommonInstalledPlugin(string PackagePath, PluginManifest Manifest)
{
    public string Sha256 { get; init; } = "";
}
