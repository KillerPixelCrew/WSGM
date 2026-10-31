using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private long _choicesRevision;
    private EmulatorCoreStatus[] _coreStatus = [];
    private EmulatorDependencyCount[] _dependencies = [];
    private bool _disposed;
    private Task? _operation;
    private CancellationTokenSource? _operationCancellation;
    private EmulatorPageState? _pageState;
    private EmulatorProgressState _progress = new(false, "");
    private long _progressRevision;
    private RomEmulatorState? _romState;
    private EmulatorSnapshot _snapshot = new();
    private IReadOnlyList<RomSystemProfile>? _systems;
    private EmulatorShortcutSync? _shortcuts;
    private string _shortcutStatus = "";

    internal EmulatorService(EmulatorManager manager, Action<string> openUrl)
    {
        _manager = manager;
        _openUrl = openUrl;
        manager.Changed += OnChanged;
        manager.InstallationsChanged += OnInstallationsChanged;
        OnChanged();
    }

    internal long CatalogRevision => Interlocked.Read(ref _catalogRevision);
    internal long ProgressRevision => Interlocked.Read(ref _progressRevision);
    internal long ChoicesRevision => Interlocked.Read(ref _choicesRevision);

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

    public event Action? Changed;

    public EmulatorSnapshot ReadState()
    {
        return _manager.GetSnapshot();
    }

    public EmulatorPageState ReadPageState()
    {
        lock (_gate)
        {
            var romState = ReadRomStateCore();
            return _pageState ??= new EmulatorPageState(_snapshot, _systems!, romState.Choices, _dependencies,
                RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")
            {
                Bios = _manager.ReadBiosState(),
                ShortcutStatus = _shortcutStatus,
                CoreStatus = _coreStatus,
                Installed = _snapshot.Installations.Select(item => new EmulatorListItem(item.Id,
                    EmulatorPresentation.Detail(item), EmulatorPresentation.Badge(_snapshot, item),
                    EmulatorPresentation.HasUpdate(_snapshot, item))).ToArray()
            };
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
        return StartOperation("release refresh", token => _manager.RefreshAsync(token), cancellationToken);
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
            token => _manager.InstallAsync(definitionId, channel, token), cancellationToken);
    }

    public Task<SteamUiCommandResult> UpdateEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return StartOperation("update " + installationId,
            token => _manager.UpdateAsync(installationId, token), cancellationToken);
    }

    public Task<SteamUiCommandResult> RepairEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return StartOperation("repair " + installationId,
            token => _manager.RepairAsync(installationId, token), cancellationToken);
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

    public async Task<SteamUiCommandResult> SetEmulatorShortcutAsync(string installationId, bool enabled, int launchMode,
        CancellationToken cancellationToken)
    {
        var wasEnabled = _manager.GetSnapshot().Installations
            .FirstOrDefault(installation => installation.Id == installationId)?.SteamShortcutEnabled == true;
        var result = await Run(() => _manager.SetSteamShortcutAsync(installationId, enabled, launchMode, cancellationToken))
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

    internal void UpdateDependencies(IReadOnlyList<EmulatorDependencyCount> dependencies)
    {
        var current = dependencies.OrderBy(item => item.InstallationId, StringComparer.Ordinal).ToArray();
        lock (_gate)
        {
            if (_dependencies.SequenceEqual(current))
            {
                return;
            }

            _dependencies = current;
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
            Interlocked.Increment(ref _choicesRevision);
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
                    new EmulatorCoreStatus(item.Id, core.Id,
                        core.RequiredFiles.Any(path => !File.Exists(path) && !Directory.Exists(path))))).ToArray();
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

            var progress = new EmulatorProgressState(snapshot.Busy, snapshot.Status);
            if (_progress != progress)
            {
                _progress = progress;
                Interlocked.Increment(ref _progressRevision);
            }
        }

        Changed?.Invoke();
    }

    internal Task<SteamUiCommandResult> StartOperation(string name, Func<CancellationToken, Task> operation,
        CancellationToken token)
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
            // Always enter the completion wrapper, even if Cancel wins before this worker starts.
            _operation = Task.Run(() => CompleteOperationAsync(name, () => operation(ownedToken), cancellation));
            _operation.ObserveFaults();
            return Task.FromResult(SteamUiCommandResult.Applied);
        }
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
            lock (_gate)
            {
                if (ReferenceEquals(_operationCancellation, cancellation))
                {
                    _operationCancellation = null;
                }
            }

            cancellation.Dispose();
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
