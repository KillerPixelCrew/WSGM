using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One plugin on the Plugins page.</summary>
/// <param name="Id">Plugin id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Section">Installed, available or unavailable.</param>
/// <param name="Badges">Status first, then version, kind, origin and validation.</param>
/// <param name="Notice">What needs attention, or empty.</param>
/// <param name="Action">The one action the row offers.</param>
/// <param name="PackagePath">The installed file, or the bundled file to install.</param>
public sealed record PluginPackageRowState(
    string Id,
    string Name,
    PluginPackageSection Section,
    IReadOnlyList<PluginBadge> Badges,
    string Notice,
    PluginPackageAction Action,
    string PackagePath)
{
    /// <summary>Whether the package requests unrestricted Steam frontend access.</summary>
    public bool SteamCef { get; init; }
}
