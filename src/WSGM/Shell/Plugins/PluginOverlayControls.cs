using System.Collections.Generic;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Host-admitted declarative controls for one common-plugin instance.</summary>
/// <param name="Actions">Named operations with validated argument declarations.</param>
/// <param name="Contributions">Host-rendered rows referencing admitted actions and effective state.</param>
/// <param name="Widgets">Compact groups of existing contribution IDs eligible for pinning.</param>
internal sealed record PluginOverlayControls(
    IReadOnlyList<PluginAction> Actions,
    IReadOnlyList<PluginUiContribution> Contributions,
    IReadOnlyList<PluginWidget> Widgets);
