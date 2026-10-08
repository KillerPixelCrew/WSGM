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

internal sealed record EmulatorPageState(
    EmulatorSnapshot Emulators,
    IReadOnlyList<RomSystemProfile> RomSystems,
    IReadOnlyList<RomSystemEmulatorChoices> Choices,
    IReadOnlyList<EmulatorDependencyCount> Dependencies,
    string Architecture)
{
    public EmulatorBiosState Bios { get; init; } = new("", false, []);
    public EmulatorListItem[] Installed { get; init; } = [];
    public EmulatorCoreStatus[] CoreStatus { get; init; } = [];
}

internal sealed record EmulatorListItem(string Id, string Detail, string Badge, bool UpdateAvailable);

internal sealed record EmulatorCoreStatus(string InstallationId, string CoreId, bool Missing);

internal sealed record EmulatorProgressState(bool Busy, string Status);

/// <summary>The emulator tool's commands, independent of library scans and Steam writes.</summary>
internal interface IEmulatorBackend : IChangeSource
{
    EmulatorSnapshot ReadState();
    EmulatorPageState ReadPageState();
    EmulatorProgressState ReadProgressState();
    Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken);
    Task<SteamUiCommandResult> RefreshEmulatorsAsync(CancellationToken cancellationToken);
    Task<SteamUiCommandResult> SetBiosFolderAsync(string path, CancellationToken cancellationToken);
    Task<SteamUiCommandResult> AddBiosFilesAsync(string path, string systemId, CancellationToken cancellationToken);
    Task<SteamUiCommandResult> VerifyBiosAsync(CancellationToken cancellationToken);
    Task<SteamUiCommandResult> RelinkBiosAsync(string systemId, CancellationToken cancellationToken);

    Task<SteamUiCommandResult> InstallEmulatorAsync(string definitionId, string channel,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> UpdateEmulatorAsync(string installationId, CancellationToken cancellationToken);
    Task<SteamUiCommandResult> RepairEmulatorAsync(string installationId, CancellationToken cancellationToken);

    Task<SteamUiCommandResult> UseExternalEmulatorAsync(string definitionId, string executable,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> RemoveEmulatorAsync(string installationId, CancellationToken cancellationToken);

    Task<SteamUiCommandResult> IgnoreEmulatorVersionAsync(string installationId, string releaseId,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> OpenEmulatorReleaseNotesAsync(string definitionId, string channel, string architecture,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> ConfigureEmulatorPrerequisiteAsync(string installationId, string path, string kind,
        CancellationToken cancellationToken);

    Task<SteamUiCommandResult> SetPreferredEmulatorAsync(string systemId, string installationId, string coreId,
        CancellationToken cancellationToken);
}

/// <summary>Projects the session-owned emulator manager into its two user interfaces.</summary>
internal sealed class EmulatorService : IEmulatorBackend, IDisposable
{
    private readonly Lock _gate = new();
    private readonly EmulatorManager _manager;
    private readonly Action<string> _openUrl;
    private EmulatorBiosState? _bios;
    private long _catalogRevision;
    private long _choicesRevision;
    private EmulatorCoreStatus[] _coreStatus = [];
    private EmulatorDependencyCount[] _dependencies = [];
    private EmulatorPageState? _pageState;
    private EmulatorProgressState _progress = new(false, "");
    private long _progressRevision;
    private RomEmulatorState? _romState;
    private EmulatorSnapshot _snapshot = new();
    private IReadOnlyList<RomSystemProfile>? _systems;

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

    public void Dispose()
    {
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
        _manager.Cancel();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> RefreshEmulatorsAsync(CancellationToken cancellationToken)
    {
        return Run(() => _manager.RefreshAsync(cancellationToken));
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
        return Run(() => _manager.InstallAsync(definitionId, channel, cancellationToken));
    }

    public Task<SteamUiCommandResult> UpdateEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return Run(() => _manager.UpdateAsync(installationId, cancellationToken));
    }

    public Task<SteamUiCommandResult> RepairEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return Run(() => _manager.RepairAsync(installationId, cancellationToken));
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

    private static async Task<SteamUiCommandResult> Run(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }
        catch (OperationCanceledException)
        {
            return new SteamUiCommandResult(false, "Emulator operation stopped.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }
    }
}
