using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Projects the session-owned emulator manager into its two user interfaces.</summary>
internal sealed class EmulatorService : IEmulatorBackend, IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly EmulatorManager _manager;
    private readonly Action<string> _openUrl;
    private EmulatorBiosState? _bios;
    private long _catalogRevision;
    private EmulatorCoreStatus[] _coreStatus = [];
    private bool _disposed;
    private Task? _operation;
    private CancellationTokenSource? _operationCancellation;
    private EmulatorPageState? _pageState;
    private EmulatorProgressState _progress = new(false, "");
    private long _progressRevision;
    private RomEmulatorState? _romState;
    private string _runningOperation = "";
    private string _runningTarget = "";
    private IReadOnlyDictionary<string, EmulatorSetupItem[]> _setup = new Dictionary<string, EmulatorSetupItem[]>();
    private string _shortcutStatus = "";
    private EmulatorShortcutSync? _shortcuts;
    private EmulatorSnapshot _snapshot = new();
    private IReadOnlyList<RomSystemProfile>? _systems;
    private EmulatorUpdateAllState? _updateAll;
    private EmulatorLibraryUsage _usage = EmulatorLibraryUsage.Empty;

    internal EmulatorService(EmulatorManager manager, Action<string> openUrl)
    {
        _manager = manager;
        _openUrl = openUrl;
        manager.Changed += OnChanged;
        manager.InstallationsChanged += OnInstallationsChanged;
        OnChanged();
    }

    internal long ProgressRevision => Interlocked.Read(ref _progressRevision);

    /// <summary>The session's admitted operation, including cancellation and staging cleanup.</summary>
    internal Task OperationCompletion
    {
        get
        {
            lock (_gate)
            {
                return _operation ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>Settles after shortcut admission closes and every dispatched Steam write has been recorded.</summary>
    internal Task ShortcutCompletion => _shortcuts?.Completion ?? Task.CompletedTask;

    public void Dispose()
    {
        Task operation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            operation = _operation ?? Task.CompletedTask;
        }

        _lifetime.Cancel();
        _shortcuts?.Dispose();
        _manager.Cancel();
        operation.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ObserveFaults();
        _manager.Changed -= OnChanged;
        _manager.InstallationsChanged -= OnInstallationsChanged;
    }

    /// <inheritdoc />
    public long CatalogRevision => Interlocked.Read(ref _catalogRevision);

    public event Action? Changed;

    public EmulatorPageState ReadPageState()
    {
        lock (_gate)
        {
            var romState = ReadRomStateCore();
            return _pageState ??= EmulatorPageProjection.Create(_snapshot, _systems!, romState,
                _manager.ReadBiosState(), _usage, _setup, _coreStatus, _updateAll, _shortcutStatus,
                EmulatorPackages.HostArchitecture);
        }
    }

    public EmulatorProgressState ReadProgressState()
    {
        lock (_gate)
        {
            return _progress;
        }
    }

    public Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _operationCancellation?.Cancel();
        }

        _manager.Cancel();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> RefreshEmulatorsAsync(CancellationToken cancellationToken)
    {
        return StartOperation("release refresh", token => _manager.RefreshAsync(token), cancellationToken,
            "refresh");
    }

    public Task<SteamUiCommandResult> SetBiosFolderAsync(string path, CancellationToken cancellationToken)
    {
        return Run(() => _manager.SetBiosFolderAsync(path, cancellationToken));
    }

    public Task<SteamUiCommandResult> AddBiosFilesAsync(string path, string systemId,
        CancellationToken cancellationToken)
    {
        return Run(() => _manager.AddBiosFilesAsync(path, systemId, cancellationToken));
    }

    public Task<SteamUiCommandResult> VerifyBiosAsync(CancellationToken cancellationToken)
    {
        return Run(() => _manager.VerifyBiosAsync(cancellationToken));
    }

    public Task<SteamUiCommandResult> RelinkBiosAsync(string systemId, CancellationToken cancellationToken)
    {
        return Run(() => _manager.RelinkBiosAsync(systemId, cancellationToken));
    }

    public Task<SteamUiCommandResult> InstallEmulatorAsync(string definitionId, string channel,
        CancellationToken cancellationToken)
    {
        return StartOperation("install " + definitionId + "/" + channel,
            token => _manager.InstallAsync(definitionId, channel, token), cancellationToken, "install", definitionId);
    }

    public Task<SteamUiCommandResult> UpdateEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return StartOperation("update " + installationId,
            token => _manager.UpdateAsync(installationId, token), cancellationToken, "update", installationId);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> UpdateAllEmulatorsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var updates = EmulatorPageProjection.PendingUpdates(_manager.GetSnapshot());
        if (updates.Length == 0)
        {
            return Task.FromResult(new SteamUiCommandResult(false,
                "No update is waiting. Check for updates to look again."));
        }

        return StartOperation("update all", token => UpdateAllAsync(updates, token), cancellationToken,
            "updateAll");
    }

    public Task<SteamUiCommandResult> RepairEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return StartOperation("repair " + installationId,
            token => _manager.RepairAsync(installationId, token), cancellationToken, "repair", installationId);
    }

    public Task<SteamUiCommandResult> UseExternalEmulatorAsync(string definitionId, string executable,
        CancellationToken cancellationToken)
    {
        return Run(() => _manager.UseExternalAsync(definitionId, executable, cancellationToken));
    }

    public Task<SteamUiCommandResult> RemoveEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return Run(() => _manager.RemoveAsync(installationId, cancellationToken));
    }

    public async Task<SteamUiCommandResult> SetEmulatorShortcutAsync(string installationId, bool enabled,
        int launchMode,
        CancellationToken cancellationToken)
    {
        var wasEnabled = _manager.GetSnapshot().Installations
            .FirstOrDefault(installation => installation.Id == installationId)?.SteamShortcutEnabled == true;
        var result = await Run(() =>
                _manager.SetSteamShortcutAsync(installationId, enabled, launchMode, cancellationToken))
            .ConfigureAwait(false);
        if (result.Succeeded)
        {
            if (enabled && !wasEnabled)
            {
                _shortcuts?.RequestExplicitCreate(installationId);
            }
            else
            {
                RequestShortcutSync();
            }
        }

        return result;
    }

    public Task<SteamUiCommandResult> IgnoreEmulatorVersionAsync(string installationId, string releaseId,
        CancellationToken cancellationToken)
    {
        return Run(() => _manager.IgnoreVersionAsync(installationId, releaseId, cancellationToken));
    }

    public Task<SteamUiCommandResult> ConfigureEmulatorPrerequisiteAsync(string installationId, string path,
        string kind, CancellationToken cancellationToken)
    {
        return Run(() => _manager.ConfigurePrerequisiteAsync(installationId, path, kind, cancellationToken));
    }

    public Task<SteamUiCommandResult> InstallEmulatorRuntimeDependenciesAsync(string definitionId,
        CancellationToken cancellationToken)
    {
        return Run(() => _manager.InstallRuntimeDependenciesAsync(definitionId, cancellationToken));
    }

    public Task<SteamUiCommandResult> SetPreferredEmulatorAsync(string systemId, string installationId, string coreId,
        CancellationToken cancellationToken)
    {
        return Run(() => _manager.SetPreferredAsync(systemId, installationId, coreId, cancellationToken));
    }

    public Task<SteamUiCommandResult> OpenEmulatorReleaseNotesAsync(string definitionId, string channel,
        string architecture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = ReadState().Offers
            .FirstOrDefault(offer => offer.DefinitionId == definitionId && offer.Channel == channel
                                                                        && offer.Architecture == architecture)
            ?.NotesUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Release notes are unavailable for this release."));
        }

        _openUrl(url);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>The manager's live snapshot, for commands that act on current state.</summary>
    internal EmulatorSnapshot ReadState()
    {
        return _manager.GetSnapshot();
    }

    /// <summary>Attaches the existing Steam writer to this session's emulator owner once.</summary>
    internal void AttachSteamShortcuts(ImportStateStore store, Func<SteamShortcutWriter?> writer,
        Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary,
        Func<uint, CancellationToken, Task<ExistingShortcut?>> readShortcut,
        Func<bool> inputManaged, Func<bool> ready,
        Func<EmulatorDefinition, uint, CancellationToken, Task<int>> artwork)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _shortcuts ??= new EmulatorShortcutSync(_manager, store, writer, readLibrary, readShortcut,
                inputManaged, ready, artwork, SetShortcutStatus, () => ShortcutsChanged?.Invoke());
        }
    }

    /// <summary>Reconciles pending shortcuts after Steam readiness, reconnect or configuration changes.</summary>
    internal void RequestShortcutSync()
    {
        _shortcuts?.Request();
    }

    private void SetShortcutStatus(string status)
    {
        lock (_gate)
        {
            if (_disposed || _shortcutStatus == status)
            {
                return;
            }

            _shortcutStatus = status;
            _pageState = null;
            Interlocked.Increment(ref _catalogRevision);
        }

        Changed?.Invoke();
    }

    internal event Action? ChoicesChanged;
    internal event Action? ShortcutsChanged;

    internal EmulatorStore ReadStore()
    {
        var snapshot = ReadState();
        return new EmulatorStore
        {
            Installations = snapshot.Installations,
            SystemPreferences = snapshot.SystemPreferences
        };
    }

    internal RomEmulatorState ReadRomState()
    {
        lock (_gate)
        {
            return ReadRomStateCore();
        }
    }

    private RomEmulatorState ReadRomStateCore()
    {
        _systems ??= RomProfiles.ForInstallations(_snapshot.Installations);
        return _romState ??= RomEmulatorProjection.Create(_snapshot, _systems);
    }

    /// <summary>
    ///     Takes the Game Library's counts: the sources and titles using each installation and each system, and the
    ///     Steam shortcut of each installation's interface.
    /// </summary>
    /// <param name="usage">The counts, read from the library's settings and import records.</param>
    internal void UpdateLibraryUsage(EmulatorLibraryUsage usage)
    {
        var current = new EmulatorLibraryUsage(
            [.. usage.Dependencies.OrderBy(item => item.InstallationId, StringComparer.Ordinal)],
            [.. usage.Systems.OrderBy(item => item.SystemId, StringComparer.Ordinal)],
            new Dictionary<string, uint>(usage.ShortcutAppIds, StringComparer.Ordinal));
        lock (_gate)
        {
            if (_usage.Dependencies.SequenceEqual(current.Dependencies)
                && _usage.Systems.SequenceEqual(current.Systems)
                && _usage.ShortcutAppIds.Count == current.ShortcutAppIds.Count
                && _usage.ShortcutAppIds.All(pair =>
                    current.ShortcutAppIds.TryGetValue(pair.Key, out var appId) && appId == pair.Value))
            {
                return;
            }

            _usage = current;
            _pageState = null;
            Interlocked.Increment(ref _catalogRevision);
        }

        Changed?.Invoke();
    }

    private void OnInstallationsChanged()
    {
        lock (_gate)
        {
            _systems = null;
            _romState = null;
            _pageState = null;
            Interlocked.Increment(ref _catalogRevision);
        }

        ChoicesChanged?.Invoke();
    }

    private void OnChanged()
    {
        var snapshot = ReadState();
        var bios = _manager.ReadBiosState();
        lock (_gate)
        {
            if (!ReferenceEquals(_snapshot.Installations, snapshot.Installations) || !ReferenceEquals(_bios, bios))
            {
                _coreStatus = snapshot.Installations.SelectMany(item => item.Cores.Select(core =>
                {
                    string[] missing =
                    [
                        .. core.RequiredFiles.Where(path => !File.Exists(path) && !Directory.Exists(path))
                            .Select(Path.GetFileName).OfType<string>()
                    ];
                    return new EmulatorCoreStatus(item.Id, core.Id, missing.Length > 0) { MissingFiles = missing };
                })).ToArray();
                _setup = snapshot.Installations.ToDictionary(item => item.Id, EmulatorPageProjection.ReadSetup,
                    StringComparer.Ordinal);
            }

            if (!ReferenceEquals(_snapshot.Definitions, snapshot.Definitions)
                || !ReferenceEquals(_snapshot.Installations, snapshot.Installations)
                || !ReferenceEquals(_snapshot.Offers, snapshot.Offers)
                || !ReferenceEquals(_snapshot.SystemPreferences, snapshot.SystemPreferences)
                || _snapshot.Initialized != snapshot.Initialized
                || !ReferenceEquals(_bios, bios))
            {
                _bios = bios;
                _snapshot = snapshot with { Busy = false, Status = "" };
                _pageState = null;
                Interlocked.Increment(ref _catalogRevision);
            }

            UpdateProgress(snapshot);
        }

        Changed?.Invoke();
    }

    /// <summary>
    ///     Republishes the progress channel from the manager's snapshot and the admitted operation, which stays busy
    ///     between the updates of an Update all run.
    /// </summary>
    /// <remarks>The caller holds <see cref="_gate" />.</remarks>
    private void UpdateProgress(EmulatorSnapshot snapshot)
    {
        var running = _runningOperation.Length > 0;
        var progress = new EmulatorProgressState(snapshot.Busy || running, snapshot.Status)
        {
            Operation = running ? _runningOperation : "",
            TargetId = running ? _runningTarget : "",
            Phase = snapshot.Phase,
            Bytes = snapshot.Bytes,
            Total = snapshot.Total
        };
        if (_progress != progress)
        {
            _progress = progress;
            Interlocked.Increment(ref _progressRevision);
        }
    }

    private void SetRunning(string operation, string target)
    {
        var snapshot = ReadState();
        lock (_gate)
        {
            _runningOperation = operation;
            _runningTarget = target;
            UpdateProgress(snapshot);
        }

        Changed?.Invoke();
    }

    private void SetUpdateAll(EmulatorUpdateAllState? state)
    {
        lock (_gate)
        {
            _updateAll = state;
            _pageState = null;
            Interlocked.Increment(ref _catalogRevision);
        }

        Changed?.Invoke();
    }

    private async Task UpdateAllAsync(IReadOnlyList<EmulatorPendingUpdate> updates, CancellationToken token)
    {
        var result = await EmulatorUpdateSequence.RunAsync(updates, (next, stepToken) =>
        {
            SetRunning("updateAll", next.InstallationId);
            return _manager.UpdateAsync(next.InstallationId, stepToken);
        }, SetUpdateAll, token).ConfigureAwait(false);
        SetRunning("updateAll", "");
        // The status line carries the whole run, so a failed update is never followed by a bare success line.
        _manager.SetStatus(result.Outcome);
        if (result.Failed.Count > 0)
        {
            Log.Warn("Emulator update all: " + result.Outcome);
        }
    }

    /// <summary>Admits one long operation as the session's owned work, refusing while another runs.</summary>
    /// <param name="name">What it is, for the log.</param>
    /// <param name="operation">The work, given the session-owned token.</param>
    /// <param name="token">The request's token; it only cancels admission.</param>
    /// <param name="kind">The progress channel's operation, such as <c>install</c>.</param>
    /// <param name="target">The definition or installation it works on.</param>
    /// <returns>Applied once admitted, or the refusal.</returns>
    internal Task<SteamUiCommandResult> StartOperation(string name, Func<CancellationToken, Task> operation,
        CancellationToken token, string kind = "", string target = "")
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Emulator manager is stopping."));
            }

            if (_operation is { IsCompleted: false } || _manager.GetSnapshot().Busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "An emulator operation is already running. Wait for it to finish or cancel it."));
            }

            // The bridge stops waiting after five seconds. Admission transfers ownership to this
            // session backend, shared with Overlay; explicit Cancel and manager disposal still stop work.
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _operationCancellation = cancellation;
            var ownedToken = cancellation.Token;
            _runningOperation = kind.Length > 0 ? kind : "operation";
            _runningTarget = target;
            if (_updateAll is not null)
            {
                // The last Update all result stays until other work starts.
                _updateAll = null;
                _pageState = null;
                Interlocked.Increment(ref _catalogRevision);
            }

            UpdateProgress(_manager.GetSnapshot());
            // Always enter the completion wrapper, even if Cancel wins before this worker starts.
            _operation = Task.Run(() => CompleteOperationAsync(name, () => operation(ownedToken), cancellation));
            _operation.ObserveFaults();
        }

        Changed?.Invoke();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private async Task CompleteOperationAsync(string name, Func<Task> operation, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await Run(operation).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                if (result.Error is { } error && _manager.GetSnapshot().Status != error)
                {
                    _manager.SetStatus(error);
                }

                Log.Warn($"Emulator {name}: {result.Error}");
            }
        }
        finally
        {
            var snapshot = ReadState();
            lock (_gate)
            {
                if (ReferenceEquals(_operationCancellation, cancellation))
                {
                    _operationCancellation = null;
                }

                _runningOperation = "";
                _runningTarget = "";
                UpdateProgress(snapshot);
            }

            cancellation.Dispose();
            Changed?.Invoke();
        }
    }

    private static async Task<SteamUiCommandResult> Run(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }
        catch (OperationCanceledException)
        {
            return new SteamUiCommandResult(false, "Emulator operation cancelled. The active version is preserved.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }
    }
}
