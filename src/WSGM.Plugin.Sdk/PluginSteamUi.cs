using System;
using System.Collections.Generic;
using SteamUiToolkit;

namespace WSGM.Plugin.Sdk;

/// <summary>The host-owned Steam UI surface a declarative plugin contribution may target.</summary>
public enum PluginSteamUiPlacement
{
    /// <summary>One entry in the shared Quick Access Extensions tab.</summary>
    ExtensionsTab,

    /// <summary>One command in Steam's context menu for a selected game.</summary>
    GameContextMenu
}

/// <summary>A bounded plugin action projected into a host-owned Steam UI surface.</summary>
/// <param name="Id">Stable contribution identity within the plugin.</param>
/// <param name="Label">Plain visible label.</param>
/// <param name="Placement">The host surface that renders the command.</param>
/// <param name="ActionId">One declared <see cref="PluginAction.Id" /> to invoke.</param>
/// <param name="AppIdArgumentKey">
///     The declared numeric action argument that receives Steam's application id for a game-menu
///     contribution. It is null for an Extensions-tab command.
/// </param>
public sealed record PluginSteamUiContribution(
    string Id,
    string Label,
    PluginSteamUiPlacement Placement,
    string ActionId,
    string? AppIdArgumentKey = null);

/// <summary>Optional declarative Steam UI contribution source.</summary>
/// <remarks>
///     The host renders the entries, validates their action links, and owns the Steam bridge. A plugin
///     never receives a React tree, webpack handle or raw JavaScript injection capability.
/// </remarks>
public interface IPluginSteamUi
{
    /// <summary>Static bounded Steam UI commands admitted before the plugin starts.</summary>
    IReadOnlyList<PluginSteamUiContribution> SteamUiContributions { get; }

    /// <summary>
    ///     Typed toolkit modules owned by the plugin. The host owns transport, bridge, patch lifecycle and
    ///     enablement; plugins own only their bounded state, commands and renderer-specific gate.
    /// </summary>
    IReadOnlyList<ISteamUiModule> SteamUiModules => [];

    /// <summary>Raised when a module publication changed and should be pushed to Steam.</summary>
    event Action? SteamUiChanged
    {
        add { }
        remove { }
    }
}
