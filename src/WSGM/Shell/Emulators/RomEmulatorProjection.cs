using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One compatible core already labelled by the host.</summary>
/// <param name="Id">The installed core identity.</param>
/// <param name="Label">The name and any missing-metadata explanation.</param>
public sealed record RomEmulatorCoreChoice(string Id, string Label);

/// <summary>One compatible installation and the cores the shared launch policy permits.</summary>
/// <param name="Id">The installation identity.</param>
/// <param name="Label">The installation's name, version and ownership.</param>
/// <param name="RequiresCore">Whether this installation launches through a core.</param>
/// <param name="DefaultCoreId">The first compatible core, or empty.</param>
/// <param name="Cores">The already ordered and labelled compatible cores.</param>
public sealed record RomEmulatorChoice(
    string Id,
    string Label,
    bool RequiresCore,
    string DefaultCoreId,
    IReadOnlyList<RomEmulatorCoreChoice> Cores);

/// <summary>Ready-to-render emulator choices for one canonical system.</summary>
/// <param name="SystemId">The system identity.</param>
/// <param name="Installations">Only installations accepted by the shared launch policy.</param>
public sealed record RomSystemEmulatorChoices(string SystemId, IReadOnlyList<RomEmulatorChoice> Installations);

/// <summary>Concrete library dependencies to show before forgetting or removing an installation.</summary>
/// <param name="InstallationId">The installation those sources and imported titles use.</param>
/// <param name="Sources">Configured ROM sources using the installation.</param>
/// <param name="Titles">Imported titles using the installation, or null when their store could not be read.</param>
public sealed record EmulatorDependencyCount(string InstallationId, int Sources, int? Titles)
{
    /// <summary>A truthful dependency description shared by both user interfaces.</summary>
    public string Summary => $"Used by {Sources} configured ROM {(Sources == 1 ? "source" : "sources")}. "
                             + (Titles is { } titles
                                 ? $"{titles} imported {(titles == 1 ? "title uses" : "titles use")} this installation."
                                 : "Imported title dependencies could not be read.");
}

/// <summary>Projects the shared launch policy once instead of repeating it in each UI.</summary>
internal static class RomEmulatorProjection
{
    internal static RomEmulatorState Create(EmulatorSnapshot snapshot, IReadOnlyList<RomSystemProfile> systems)
    {
        var choices = systems.Select(system => new RomSystemEmulatorChoices(system.Id,
            snapshot.Installations.Where(installation => EmulatorStorage.SupportsSystem(installation, system.Id))
                .Select(installation =>
                {
                    var cores = installation.DataPolicy.HasCores
                        ? EmulatorStorage.CompatibleCores(installation, system.Id)
                            .OrderBy(core => core.MetadataMissing || core.Systems.Length == 0).ThenBy(core => core.Name)
                            .ToArray()
                        : [];
                    return new RomEmulatorChoice(installation.Id,
                        installation.Name + " " + installation.Version +
                        (installation.Managed ? " · Managed" : " · External"),
                        installation.DataPolicy.HasCores, cores.FirstOrDefault()?.Id ?? "",
                        cores.Select(core => new RomEmulatorCoreChoice(core.Id,
                            core.Name + (core.MetadataMissing || core.Systems.Length == 0
                                ? " · System compatibility unavailable"
                                : ""))).ToArray());
                }).ToArray())).ToArray();
        EmulatorStore preferences = new() { SystemPreferences = snapshot.SystemPreferences };
        return new RomEmulatorState(choices, snapshot.SystemPreferences
            .Select(preference => EmulatorStorage.NormalizeSystemId(preference.SystemId)).Distinct()
            .Select(system => ManagedContentStorage.PreferredSystem(preferences, system)! with { SystemId = system })
            .ToArray());
    }
}
