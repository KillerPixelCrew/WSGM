using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Keeps WSGM's ES-DE in step with the ROM sources and installed emulators: its ROM folder of links, its custom
///     systems file and its ROM folder setting. One coalescing worker; requests while it runs fold into one more pass.
/// </summary>
/// <remarks>
///     Nothing is written until the library's ROM sources are known, so a session that has not read them yet never
///     empties ES-DE. The emulator admission gate is not taken: launches never wait behind a sync, and an install
///     that changes paths raises its own sync when it publishes.
/// </remarks>
internal sealed class EsDeSync : IDisposable
{
    private readonly Action _changed;
    private readonly Action<string, string> _createLink;
    private readonly Lock _gate = new();
    private readonly Func<string, bool> _running;
    private readonly Func<EmulatorSnapshot> _snapshot;
    private readonly string _userRoot;
    private HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _pending;
    private bool _replaceRomFolder;
    private RomSourceConfig[]? _sources;
    private EsDeState _state = EsDeState.NotInstalled;
    private Task _worker = Task.CompletedTask;

    /// <summary>Creates the owner; it does nothing until ROM sources arrive.</summary>
    /// <param name="userRoot">The user's WSGM data root.</param>
    /// <param name="snapshot">The emulator manager's current installations and preferences.</param>
    /// <param name="changed">Raised on the worker after the published state changes.</param>
    /// <param name="createLink">Creates a directory link; tests replace it.</param>
    /// <param name="running">Whether the ES-DE executable at a path is running; tests replace it.</param>
    internal EsDeSync(string userRoot, Func<EmulatorSnapshot> snapshot, Action changed,
        Action<string, string>? createLink = null, Func<string, bool>? running = null)
    {
        _userRoot = userRoot;
        _snapshot = snapshot;
        _changed = changed;
        _createLink = createLink ?? EmulatorPortableSetup.CreateAlias;
        _running = running ?? Running;
    }

    /// <summary>The state both surfaces show.</summary>
    internal EsDeState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The current pass, settled when no sync is running.</summary>
    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _worker;
            }
        }
    }

    /// <summary>Stops accepting passes; a running pass finishes its current file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    /// <summary>Takes the library's ROM sources and its Steam choices, and syncs ES-DE with them.</summary>
    /// <param name="settings">The library's settings in force.</param>
    internal void UpdateSources(GameLibraryConfig settings)
    {
        var sources = settings.RomSources.Select(source => source.Copy()).ToArray();
        var disabled = settings.DisabledSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _sources = sources;
            _disabled = disabled;
        }

        // Always a pass: a card mounted under another letter changes no setting but needs its link moved.
        Request();
    }

    /// <summary>Asks for a pass.</summary>
    /// <param name="replaceRomFolder">Whether to point ES-DE back at WSGM's folder when the user chose another.</param>
    internal void Request(bool replaceRomFolder = false)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;
            _replaceRomFolder |= replaceRomFolder;
            if (_worker.IsCompleted)
            {
                _worker = Task.Run(Run);
            }
        }
    }

    private void Run()
    {
        while (true)
        {
            RomSourceConfig[] sources;
            HashSet<string> disabled;
            bool replace;
            lock (_gate)
            {
                if (!_pending || _disposed || _sources is null)
                {
                    _pending = false;
                    return;
                }

                _pending = false;
                replace = _replaceRomFolder;
                _replaceRomFolder = false;
                sources = _sources;
                disabled = _disabled;
            }

            EsDeState state;
            try
            {
                state = Apply(_snapshot(), sources, disabled, replace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or InvalidOperationException or ArgumentException)
            {
                Log.Warn("ES-DE could not be set up: " + ex.Message);
                state = State with
                {
                    Status = "ES-DE could not be set up: " + ex.Message + " Sync now tries again.", Tone = "bad"
                };
            }

            bool changed;
            lock (_gate)
            {
                changed = !Same(_state, state);
                _state = state;
            }

            if (changed)
            {
                _changed();
            }
        }
    }

    private EsDeState Apply(EmulatorSnapshot snapshot, RomSourceConfig[] sources, HashSet<string> disabled,
        bool replace)
    {
        var esDe = snapshot.Installations.FirstOrDefault(item =>
            item is { Managed: true, DefinitionId: EsDeConfiguration.DefinitionId });
        if (esDe is null)
        {
            return EsDeState.NotInstalled;
        }

        var store = new EmulatorStore
        {
            Installations = snapshot.Installations, SystemPreferences = snapshot.SystemPreferences
        };
        var profiles = RomProfiles.ForInstallations(snapshot.Installations);
        var folders = EsDeFiles.Folders(sources, profiles);
        var program = Path.GetDirectoryName(esDe.ExecutablePath)!;
        var plan = EsDeConfiguration.Build(folders, store, profiles, EsDeFiles.ReadBundled(program));
        var romFolder = EsDeFiles.RomFolder(_userRoot);
        var problems = EsDeFiles.ReconcileLinks(romFolder, plan.Links, _createLink);
        var written = EsDeFiles.WriteSystems(esDe.DataPath, plan);
        var setting = _running(esDe.ExecutablePath)
            ? Pending(EsDeFiles.ReadRomDirectory(esDe.DataPath), romFolder, replace)
            : EsDeFiles.SetRomDirectory(esDe.DataPath, romFolder, replace);
        if (written || setting == EsDeRomFolderSetting.Set)
        {
            Log.Info($"ES-DE configured: {plan.Systems.Count(system => system.Shown)} systems, "
                     + $"{plan.Links.Length} folder links.");
        }

        return Project(plan, sources, folders, disabled, romFolder, setting, esDe.DataPath, problems);
    }

    private static EsDeState Project(EsDePlan plan, RomSourceConfig[] sources, List<EsDeFolder> folders,
        HashSet<string> disabled, string romFolder, EsDeRomFolderSetting setting, string dataPath,
        List<string> problems)
    {
        var shown = plan.Systems.Where(system => system.Shown).ToArray();
        var summary = sources.Length == 0
            ? "No ROM folders yet"
            : shown.Length == 0
                ? "No system is ready yet"
                : EmulatorPresentation.Count(shown.Length, "system") + " from "
                                                                     + EmulatorPresentation.Count(sources.Length,
                                                                         "ROM folder");
        var (status, tone) = setting switch
        {
            EsDeRomFolderSetting.Elsewhere => (
                "ES-DE reads games from " + EsDeFiles.ReadRomDirectory(dataPath)
                                          + ", which was chosen in ES-DE. Sync now switches it to your WSGM ROM folders.",
                "warn"),
            EsDeRomFolderSetting.Waiting => (
                "ES-DE is open. Close it, then choose Sync now to point it at your WSGM ROM folders.", "warn"),
            _ when problems.Count > 0 => (string.Join(" ", problems), "warn"),
            _ => ("", "")
        };
        EsDeSystemRow Row(EsDeSystem system)
        {
            var connected = system.Folders.Count(folder => folder.Path.Length > 0);
            var others = system.Commands.Length - 1;
            var detail = system.Shown
                ? system.Commands[0].Label
                  + (others > 0 ? ", and " + EmulatorPresentation.Count(others, "more") + " in ES-DE" : "")
                  + " · " + EmulatorPresentation.Count(system.Folders.Length, "folder")
                  + (connected < system.Folders.Length ? ", " + (system.Folders.Length - connected) + " not connected" : "")
                : system.Problem;
            return new EsDeSystemRow(system.SystemId, system.FullName, detail, system.Problem);
        }

        return new EsDeState(true, summary, status, tone)
        {
            RomFolder = romFolder,
            Synced = true,
            Systems = [.. plan.Systems.Select(Row)],
            Folders =
            [
                .. sources.Select(source =>
                {
                    var own = folders.Where(folder => folder.SourceId == source.Id).ToArray();
                    var connected = own.Any(folder => folder.Path.Length > 0);
                    var names = plan.Systems.Where(system => system.Folders.Any(folder => folder.SourceId == source.Id))
                        .Select(system => system.FullName).ToArray();
                    var systems = !connected
                        ? "Not connected"
                        : names.Length == 0
                            ? "No system folders found"
                            : EmulatorPresentation.JoinAnd(names);
                    return new EsDeFolderRow(source.Id, source.Name, source.Root.AbsolutePath, systems, connected,
                        !disabled.Contains(source.Id));
                })
            ]
        };
    }

    /// <summary>What a running ES-DE's setting needs: ES-DE rewrites its settings, so nothing is written meanwhile.</summary>
    private static EsDeRomFolderSetting Pending(string current, string romFolder, bool replace)
    {
        if (EsDeFiles.SamePath(current, romFolder))
        {
            return EsDeRomFolderSetting.Unchanged;
        }

        return current.Trim().Length > 0 && !replace ? EsDeRomFolderSetting.Elsewhere : EsDeRomFolderSetting.Waiting;
    }

    private static bool Same(EsDeState left, EsDeState right)
    {
        return left.Installed == right.Installed && left.Summary == right.Summary && left.Status == right.Status
               && left.Tone == right.Tone && left.RomFolder == right.RomFolder && left.Synced == right.Synced
               && left.Systems.SequenceEqual(right.Systems) && left.Folders.SequenceEqual(right.Folders);
    }

    private static bool Running(string executable)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
