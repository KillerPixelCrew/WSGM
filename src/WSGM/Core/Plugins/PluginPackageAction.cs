namespace WSGM.Core;

/// <summary>What the Plugins page can do with one row.</summary>
public enum PluginPackageAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Copy a bundled package into the Plugins folder.</summary>
    Install,

    /// <summary>Remove an installed package file.</summary>
    Remove
}
