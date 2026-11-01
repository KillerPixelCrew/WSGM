using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The parts of an Epic installed-game manifest discovery reads.</summary>
/// <param name="AppName">The launcher's stable id for the title.</param>
/// <param name="DisplayName">Its name, or empty.</param>
/// <param name="InstallLocation">Where it is installed, or empty.</param>
/// <param name="LaunchExecutable">Its executable, relative to the install location, or empty.</param>
/// <param name="LaunchCommand">The arguments the launcher passes it, or empty.</param>
/// <param name="CatalogNamespace">Its catalog namespace, part of the launch URI.</param>
/// <param name="CatalogItemId">Its catalog item id, part of the launch URI.</param>
/// <param name="AppCategories">Its categories, such as <c>games</c> or <c>addons</c>.</param>
/// <param name="CompatibleApps">The apps it plugs into, <c>UE_*</c> for engine plugins.</param>
/// <param name="TechnicalType">Its technical type, or empty.</param>
/// <param name="IncompleteInstall">Whether the launcher is still installing it.</param>
internal sealed record EpicManifest(
    string AppName,
    string DisplayName,
    string InstallLocation,
    string LaunchExecutable,
    string LaunchCommand,
    string CatalogNamespace,
    string CatalogItemId,
    IReadOnlyList<string> AppCategories,
    IReadOnlyList<string> CompatibleApps,
    string TechnicalType,
    bool IncompleteInstall = false);
