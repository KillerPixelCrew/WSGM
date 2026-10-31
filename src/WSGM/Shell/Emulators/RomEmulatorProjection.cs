using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

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
