using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The Game Library's view of the session's emulators: the ROM editor choices and parser systems,
///     cached until the emulator manager reports a change, the installed emulators' store, and the
///     dependency counts the emulator manager shows for each installation.
/// </summary>
/// <param name="service">The session-owned emulator manager, or null in an isolated importer.</param>
internal sealed class GameLibraryEmulators(EmulatorService? service)
{
    private readonly Lock _gate = new();
    private RomEmulatorState? _romEmulators;
    private IReadOnlyList<RomSystemProfile>? _romSystems;

    /// <summary>The emulator manager whose changes the importer follows, or null in an isolated importer.</summary>
    internal EmulatorService? Service { get; } = service;

    /// <summary>The cached ROM editor choices and parser systems, read from the emulator manager once per change.</summary>
    /// <returns>Both, from the same cache generation.</returns>
    internal (RomEmulatorState State, IReadOnlyList<RomSystemProfile> Systems) Read()
    {
        lock (_gate)
        {
            _romEmulators ??= Service?.ReadRomState() ?? RomEmulatorState.Empty;
            _romSystems ??= Service?.ReadPageState().RomSystems ?? RomProfiles.All;
            return (_romEmulators, _romSystems);
        }
    }

    /// <summary>The parser systems last read, or the built-in profiles before any read.</summary>
    internal IReadOnlyList<RomSystemProfile> CachedSystems
    {
        get
        {
            lock (_gate)
            {
                return _romSystems ?? RomProfiles.All;
            }
        }
    }

    /// <summary>Drops the cached choices and systems, so the next read asks the emulator manager again.</summary>
    internal void Invalidate()
    {
        lock (_gate)
        {
            _romEmulators = null;
            _romSystems = null;
        }
    }

    /// <summary>The installed emulators' store, or an empty one in an isolated importer.</summary>
    /// <returns>The store as the emulator manager reads it now.</returns>
    internal EmulatorStore ReadStore()
    {
        return Service?.ReadStore() ?? new EmulatorStore();
    }

    /// <summary>Counts emulator dependencies again: the sources that select each installation and the imported titles.</summary>
    /// <param name="settings">The library's settings in force.</param>
    /// <param name="imports">The import records the titles are counted from.</param>
    internal void UpdateDependencies(GameLibraryConfig settings, ImportStateStore imports)
    {
        if (Service is null)
        {
            return;
        }

        var store = ReadStore();

        string Selected(string systemId, string installationId)
        {
            return installationId.Length > 0
                ? installationId
                : ManagedContentStorage.PreferredSystem(store, systemId)?.InstallationId ?? "";
        }

        var sources = settings.RomSources.Select(source => Selected(source.SystemId, source.EmulatorInstallationId))
            .Concat(settings.ManualSources.Where(source => source.RomPath is not null)
                .Select(source => Selected(source.SystemId, source.EmulatorInstallationId)))
            .GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var titles = imports.CountEmulatorDependencies(store);

        Service.UpdateDependencies(store.Installations.Select(installation => new EmulatorDependencyCount(
            installation.Id, sources.GetValueOrDefault(installation.Id),
            titles?.GetValueOrDefault(installation.Id))).ToArray());
    }

    /// <summary>The emulator, core and arguments a ROM's source gives it when the title has no override.</summary>
    /// <param name="settings">The library's settings in force.</param>
    /// <param name="content">The ROM's managed content.</param>
    /// <returns>The source's choices; empty when the source is gone.</returns>
    internal static (string Installation, string Core, IReadOnlyList<string> Arguments) RomDefaults(
        GameLibraryConfig settings, ManagedContentRecord content)
    {
        if (content.SourceId == "manual")
        {
            var manual = settings.ManualSources.FirstOrDefault(source => source.Id == content.SourceKey);
            return (manual?.EmulatorInstallationId ?? "", manual?.CoreId ?? "", manual?.RomArguments ?? []);
        }

        var library = settings.RomSources.FirstOrDefault(source => source.Id == content.SourceId);
        return (library?.EmulatorInstallationId ?? "", library?.CoreId ?? "", library?.Arguments ?? []);
    }
}
