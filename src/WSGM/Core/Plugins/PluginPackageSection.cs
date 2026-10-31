namespace WSGM.Core;

/// <summary>Which part of the Plugins page a row belongs to.</summary>
public enum PluginPackageSection
{
    /// <summary>A file in the Plugins folder, loaded or not.</summary>
    Installed,

    /// <summary>Bundled with this release and installable here.</summary>
    Available,

    /// <summary>Bundled but not for this hardware, or outdated.</summary>
    Unavailable
}
