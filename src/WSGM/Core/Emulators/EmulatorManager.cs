using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>One per-user owner for emulator releases, portable transactions and local installation state.</summary>
public sealed partial class EmulatorManager : IDisposable
{
    private readonly UserDataContext _context;
    private readonly ImportStateStore? _importState;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly EmulatorNetwork _network;
    private readonly SemaphoreSlim _operations = new(1);
    private readonly EmulatorPackages _packages;
    private readonly EmulatorReleases _releases;
    private readonly object _stateLock = new();
    private CancellationTokenSource? _active;
    private EmulatorCatalog _catalog = new();
    private EmulatorDefinition[] _definitionViews = [];
    private volatile bool _disposed;
    private bool _initialized;
    private bool _initializing = true;
    private EmulatorStore _state = new();
    private string _status = "";

    /// <summary>Starts owned background initialization and creates the session release transport.</summary>
    public EmulatorManager(UserDataContext context, HttpMessageHandler? handler = null)
        : this(context, handler, null)
    {
    }

    internal EmulatorManager(UserDataContext context, HttpMessageHandler? handler, ImportStateStore? importState)
    {
        _context = context;
        _importState = importState;
        _network = new EmulatorNetwork(handler);
        _releases = new EmulatorReleases(_network);
        _packages = new EmulatorPackages(_network);
        Initialization = Task.Run(InitializeLocal);
    }

    private string ProgramRoot => Path.Combine(_context.Root, "Emulators");
    private string DataRoot => Path.Combine(_context.Root, "EmulatorData");

    internal bool Initialized
    {
        get
        {
            lock (_stateLock)
            {
                return _initialized;
            }
        }
    }

    internal Task Initialization { get; }

    /// <summary>Closes admission, cancels active work and releases the transport.</summary>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        Cancel();
        _network.Dispose();
        _lifetime.Dispose();
    }

    private IReadOnlyList<string> KnownRomPaths(EmulatorInstallation? previous)
    {
        if (previous is null || previous.DefinitionId != "retroarch" || _importState is null)
        {
            return [];
        }

        var store = EmulatorStorage.ReadStore(_context.Root);
        var paths = new List<string>();
        var checks = new ManagedContentCheckContext();
        foreach (var entry in _importState.Entries())
        {
            if (entry.Content is not { SourceKind: LibrarySourceKind.Rom } content ||
                ManagedContentStorage.ResolveEmulatorPreference(store, content).EmulatorInstallationId != previous.Id)
            {
                continue;
            }

            try
            {
                paths.Add(ManagedContentStorage.ResolvePath(content.BackingPath, checks));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                              or InvalidOperationException)
            {
                throw new IOException($"Reconnect the ROM media for {entry.Name} before converting RetroArch's "
                                      + "content-directory saves to portable storage. The active version is preserved.",
                    error);
            }
        }

        return paths;
    }

    private void InitializeLocal()
    {
        var catalog = EmulatorCatalog.LoadBundled();
        lock (_stateLock)
        {
            _catalog = catalog;
            _definitionViews = catalog.Definitions.Select(definition => definition.View()).ToArray();
        }

        LoadLocal();
        try
        {
            VerifyBios(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("BIOS files could not be checked: " + ex.Message);
        }

        lock (_stateLock)
        {
            _initializing = false;
        }

        NotifyChanged();
    }

    private void LoadLocal()
    {
        try
        {
            var state = EmulatorStorage.ReadStore(_context.Root);
            lock (_stateLock)
            {
                _state = state;
                _initialized = true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or JsonException)
        {
            // A later mutation still performs a strict fresh read; this projection never overwrites unreadable state.
            SetStatus("Emulator state could not be read: " + ex.Message);
            return;
        }

        try
        {
            RecoverTransactions(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Emulator cleanup could not complete: " + ex.Message);
        }

        NotifyChanged();
        NotifyInstallationsChanged();
    }

    /// <summary>Invalidates UI snapshots after a state or progress change; raised on the worker.</summary>
    public event Action? Changed;

    /// <summary>Raised only when durable installation or system-preference semantics change.</summary>
    public event Action? InstallationsChanged;

    /// <summary>Returns detached cached definitions, installations, offers and current progress.</summary>
    public EmulatorSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new EmulatorSnapshot
            {
                Definitions = _definitionViews,
                BiosFolder = _state.BiosFolder,
                Installations = _state.Installations,
                Offers = _state.Offers,
                Initialized = _initialized,
                Busy = _active is not null || _initializing,
                Status = _status,
                SystemPreferences = _state.SystemPreferences
            };
        }
    }

    /// <summary>Checks release metadata and known external locations without installing anything.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        return OperationAsync(async token =>
        {
            if (!GetSnapshot().Initialized)
            {
                LoadLocal();
            }

            await Task.Run(() => RecoverTransactions(token), token).ConfigureAwait(false);

            var requests = _catalog.Definitions.SelectMany(definition =>
                definition.Sources.Keys.Select(channel => (definition, channel)));
            using SemaphoreSlim requestsGate = new(3);
            var checks = requests.Select(async request =>
            {
                var (definition, channel) = request;
                var targetArchitecture =
                    RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                        ? "arm64"
                        : "x64";
                await requestsGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    SetStatus("Checking " + definition.Name + " (" + channel + ")");
                    try
                    {
                        var architecture = Architecture(definition);
                        var release = await _releases.FindAsync(definition, channel, architecture, token)
                            .ConfigureAwait(false);
                        return new EmulatorOffer
                        {
                            DefinitionId = definition.Id, Channel = channel, Architecture = architecture,
                            Version = release.Version, ReleaseId = release.Id, NotesUrl = release.NotesUrl
                        };
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException
                                                   or InvalidDataException
                                                   or JsonException or InvalidOperationException)
                    {
                        return new EmulatorOffer
                        {
                            DefinitionId = definition.Id, Channel = channel, Architecture = targetArchitecture,
                            Error = ex.Message
                        };
                    }
                }
                finally
                {
                    requestsGate.Release();
                }
            }).ToArray();
            var offers = await Task.WhenAll(checks).ConfigureAwait(false);

            Mutate(store => store with
            {
                Offers = [.. offers],
                Installations = store.Installations.Select(EmulatorPrerequisites.RefreshState).ToArray()
            });
            await DiscoverExternalAsync(token).ConfigureAwait(false);
            VerifyBios(token);
            SetStatus("Emulator versions checked.");
        }, cancellationToken);
    }

    /// <summary>Downloads and activates a reviewed managed emulator, including the complete RetroArch core catalogue.</summary>
    public Task InstallAsync(string definitionId, string channel, CancellationToken cancellationToken)
    {
        return OperationAsync(token => InstallCoreAsync(Definition(definitionId), channel, null, token),
            cancellationToken);
    }

    /// <summary>Stages the selected channel's current package and preserves the installed identity and data.</summary>
    public Task UpdateAsync(string installationId, CancellationToken cancellationToken)
    {
        return OperationAsync(token =>
        {
            var installed = Installation(installationId);
            RequireManaged(installed);
            return InstallCoreAsync(Definition(installed.DefinitionId), installed.Channel, installed, token);
        }, cancellationToken);
    }

    /// <summary>Reinstalls a managed program through the same verified transaction while preserving user data.</summary>
    public Task RepairAsync(string installationId, CancellationToken cancellationToken)
    {
        return OperationAsync(token =>
        {
            var installed = Installation(installationId);
            RequireManaged(installed);
            return InstallCoreAsync(Definition(installed.DefinitionId), installed.Channel, installed, token,
                true);
        }, cancellationToken);
    }

    /// <summary>Records a validated external executable without taking ownership of files or updates.</summary>
    public Task UseExternalAsync(string definitionId, string executable, CancellationToken cancellationToken)
    {
        return OperationAsync(async token =>
        {
            await RegisterExternalAsync(Definition(definitionId), executable, token).ConfigureAwait(false);
            if (Directory.Exists(BiosFolder))
            {
                RelinkBios(token, installationId: ExternalId(definitionId, Path.GetFullPath(executable)));
            }

            VerifyBios(token);
        }, cancellationToken);
    }

    /// <summary>Removes owned program files or forgets an external registration, preserving user data.</summary>
    public Task RemoveAsync(string installationId, CancellationToken cancellationToken)
    {
        return OperationAsync(async token =>
        {
            var installed = Installation(installationId);
            await Task.Run(() => Admission(token, () =>
            {
                if (installed.Managed)
                {
                    EmulatorPrerequisites.EnsureStopped(installed);
                    PreserveVersions(installed, token);
                }

                Mutate(store => store with
                {
                    Installations = store.Installations.Where(item => item.Id != installed.Id).ToArray(),
                    ForgottenExternalIds = installed.Managed
                        ? store.ForgottenExternalIds
                        : [.. store.ForgottenExternalIds.Where(id => id != installed.Id), installed.Id]
                });
                if (installed.Managed)
                {
                    DeleteRetired(installed.Root, ProgramRoot);
                }
            }), token).ConfigureAwait(false);
            SetStatus("Emulator removed. User data and imported titles are preserved.");
        }, cancellationToken);
    }

    /// <summary>Persists an exact channel/artifact release identity to skip in the update offer.</summary>
    public Task IgnoreVersionAsync(string installationId, string releaseId, CancellationToken cancellationToken)
    {
        return OperationAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            Mutate(store => store with
            {
                Installations = store.Installations.Select(item => item.Id == installationId
                    ? item with { IgnoredReleaseId = releaseId }
                    : item).ToArray()
            }, false);
            SetStatus("This release will be skipped.");
            return Task.CompletedTask;
        }, cancellationToken);
    }

    /// <summary>Persists the preferred installed emulator and optional RetroArch core for one ROM system.</summary>
    public Task SetPreferredAsync(string systemId, string installationId, string coreId,
        CancellationToken cancellationToken)
    {
        return OperationAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            systemId = EmulatorStorage.NormalizeSystemId(systemId);
            if (string.IsNullOrWhiteSpace(installationId))
            {
                Mutate(store => store with
                {
                    SystemPreferences = store.SystemPreferences.Where(item =>
                        EmulatorStorage.NormalizeSystemId(item.SystemId) != systemId).ToArray()
                });
                SetStatus("Preferred emulator cleared for " + systemId + ".");
                return Task.CompletedTask;
            }

            var installed = Installation(installationId);
            EmulatorStorage.ValidateSelection(installed, systemId, coreId);

            Mutate(store => store with
            {
                SystemPreferences =
                [
                    .. store.SystemPreferences.Where(item =>
                        EmulatorStorage.NormalizeSystemId(item.SystemId) != systemId),
                    new EmulatorSystemPreference
                        { SystemId = systemId, InstallationId = installationId, CoreId = coreId }
                ]
            });
            SetStatus("Preferred emulator saved for " + systemId + ".");
            return Task.CompletedTask;
        }, cancellationToken);
    }

    /// <summary>Supplies local BIOS/keys/system files or opens the emulator-owned native firmware installer.</summary>
    public Task ConfigurePrerequisiteAsync(string installationId, string path, string kind,
        CancellationToken cancellationToken)
    {
        return OperationAsync(async token =>
        {
            var installed = Installation(installationId);
            await Task.Run(() => Admission(token, () =>
            {
                var configured =
                    EmulatorPrerequisites.RefreshState(EmulatorPrerequisites.Configure(installed, path, kind, token));
                Mutate(store => store with
                {
                    Installations = store.Installations.Select(item => item.Id == installed.Id ? configured : item)
                        .ToArray()
                });
            }), token).ConfigureAwait(false);
            VerifyBios(token);
            SetStatus(installed.DataPolicy.Prerequisites.Any(rule => rule.Kind == kind && rule.NativeInstaller)
                ? "Local firmware installed. The emulator checks decryption and key compatibility when started."
                : "Local prerequisite supplied. Existing saves and settings are preserved.");
        }, cancellationToken);
    }

    /// <summary>Requests cancellation of the admitted background operation.</summary>
    public void Cancel()
    {
        lock (_stateLock)
        {
            _active?.Cancel();
        }
    }

    private async Task InstallCoreAsync(EmulatorPackageDefinition definition, string channel,
        EmulatorInstallation? previous, CancellationToken cancellationToken, bool repair = false)
    {
        previous ??= EmulatorStorage.ReadInstallations(_context.Root)
            .FirstOrDefault(item => item.Managed && item.DefinitionId == definition.Id && item.Channel == channel);
        var id = previous?.Id ?? definition.Id + "-" + Guid.NewGuid().ToString("N");
        var architecture = previous?.Architecture ?? Architecture(definition);
        SetStatus("Finding " + definition.Name + " (" + channel + ")");
        var release = repair && previous is not null
            ? new EmulatorRelease(previous.Version, previous.ReleaseId, "", previous.Source,
                new EmulatorAsset(previous.PackageName,
                    previous.SourceUrl,
                    previous.ExpectedSha256.Length > 0 ? previous.ExpectedSha256 : previous.Sha256))
            : await _releases.FindAsync(definition, channel, architecture, cancellationToken).ConfigureAwait(false);
        if (!Path.GetExtension(release.Asset.Name)
                .Equals("." + definition.PackageType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The upstream asset does not match this definition's reviewed package format.");
        }

        var installRoot = Path.Combine(ProgramRoot, id);
        var stage = Path.Combine(installRoot, "staging-" + Guid.NewGuid().ToString("N"));
        var program = Path.Combine(stage, "program");
        var downloads = Path.Combine(stage, "downloads");
        var data = Path.Combine(DataRoot, id);
        const bool ownsData = true;
        var archive = Path.Combine(downloads, release.Asset.Name);
        try
        {
            Directory.CreateDirectory(downloads);
            SetStatus("Downloading " + definition.Name + " " + release.Version);
            var hash = repair && previous is not null
                ? await _packages.RestoreArchiveAsync(previous, release.Asset.Name, archive, release.Asset.Url,
                    release.Asset.Sha256, cancellationToken).ConfigureAwait(false)
                : await _packages.DownloadAsync(release.Asset, archive, cancellationToken).ConfigureAwait(false);
            SetStatus("Extracting " + definition.Name);
            await Task.Run(() => EmulatorPackages.Extract(archive, program, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (release.ExtractDirectory.Length > 0)
            {
                var expected = Path.GetFullPath(Path.Combine(program, release.ExtractDirectory));
                if (!StoragePaths.IsUnder(program, expected)
                    || !Directory.Exists(expected))
                {
                    throw new InvalidDataException(
                        "The package's declared extraction directory is missing or escapes staging.");
                }
            }

            var executable = EmulatorPackages.FindExecutable(program, definition, architecture);
            var actualProgram = Path.GetDirectoryName(executable)!;
            EmulatorCore[] cores = [];
            if (definition.DataPolicy.HasCores)
            {
                cores = repair && previous is not null
                    ? await _packages.RepairCoresAsync(previous, actualProgram, downloads, SetStatus, cancellationToken)
                        .ConfigureAwait(false)
                    : await _packages.InstallCoresAsync(release.CoreIndexUrl, release.CoreAssetsUrl, actualProgram,
                        downloads, data,
                        SetStatus, cancellationToken).ConfigureAwait(false);
            }

            if (definition.DataPolicy.NativeRoots.Length > 0)
            {
                // Native data is outside immutable versions; a bundled portable marker would silently change ownership.
                if (definition.DataPolicy.PortableMarkers.Select(marker => Path.Combine(actualProgram, marker))
                    .Any(path => Directory.Exists(path) || File.Exists(path)))
                {
                    throw new InvalidDataException(
                        "This package forces portable data. A reviewed data migration is required before installing it.");
                }
            }

            var versionRoot = Path.Combine(installRoot, "versions", Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(release.Id)))[..20] + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.GetDirectoryName(versionRoot)!);
            var relativeExecutable = Path.GetRelativePath(program, executable);
            EmulatorInstallation candidate = new()
            {
                Id = id, DefinitionId = definition.Id, Name = definition.Name, Managed = true,
                Channel = channel, Architecture = architecture, Version = release.Version, ReleaseId = release.Id,
                Root = installRoot, ExecutablePath = Path.Combine(versionRoot, relativeExecutable), DataPath = data,
                OwnsData = ownsData, PreviousExecutablePath = previous?.ExecutablePath ?? "",
                SourceUrl = release.Asset.Url,
                PackageName = release.Asset.Name,
                Source = release.Provider, Sha256 = hash, ExpectedSha256 = release.Asset.Sha256,
                Integrity =
                    release.Asset.Sha256.Length > 0 ? "Verified SHA-256" : "HTTPS; no upstream SHA-256 supplied",
                Systems = definition.Systems,
                LaunchArguments = [.. definition.DataPolicy.DataArguments, .. definition.LaunchArguments],
                DataPolicy = definition.DataPolicy,
                Environment = new Dictionary<string, string>(definition.DataPolicy.Environment),
                ConfiguredPrerequisites = previous is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(previous.ConfiguredPrerequisites),
                Cores = cores.Select(core => core with
                {
                    Path = Path.Combine(versionRoot, Path.GetRelativePath(program, core.Path))
                }).ToArray()
            };
            candidate = candidate with
            {
                ExpectedSha256 = repair && previous is not null ? previous.ExpectedSha256 : release.Asset.Sha256,
                Integrity = repair && previous is not null ? previous.Integrity : candidate.Integrity,
                PackageCachePath = Path.Combine(versionRoot, ".packages"),
                CoreCatalogueRevision = repair && previous is not null
                    ? previous.CoreCatalogueRevision
                    : _packages.CoreCatalogueRevision
            };
            await Task.Run(() => Admission(cancellationToken, () =>
            {
                if (previous is not null)
                {
                    var current = EmulatorStorage.ReadInstallations(_context.Root)
                                      .SingleOrDefault(item => item.Id == id)
                                  ?? throw new InvalidOperationException(
                                      "The installation was removed while the update was prepared. Refresh before retrying.");
                    if (current.ExecutablePath != previous.ExecutablePath || current.DataPath != previous.DataPath)
                    {
                        throw new InvalidOperationException(
                            "The installation changed while the update was prepared. Refresh before retrying.");
                    }

                    candidate = candidate with
                    {
                        ConfiguredPrerequisites = new Dictionary<string, string>(current.ConfiguredPrerequisites)
                    };
                }

                Directory.Move(program, versionRoot);
                var published = false;
                try
                {
                    SetStatus("Checking " + definition.Name + " startup before activation");
                    EmulatorValidation.ProbeAsync(definition, candidate, cancellationToken).GetAwaiter().GetResult();
                    if (previous is not null)
                    {
                        PreserveLegacyRpcData(previous, data, cancellationToken);
                    }

                    candidate = EmulatorPortableSetup.Prepare(candidate, data, previous, cancellationToken, true,
                        () => KnownRomPaths(previous));
                    if (previous is not null)
                    {
                        candidate = candidate with
                        {
                            ConfiguredPrerequisites = candidate.ConfiguredPrerequisites.ToDictionary(pair => pair.Key,
                                pair => Path.IsPathFullyQualified(pair.Value) &&
                                        StoragePaths.IsUnder(previous.DataPath, pair.Value)
                                    ? Path.Combine(candidate.DataPath,
                                        Path.GetRelativePath(previous.DataPath, pair.Value))
                                    : pair.Value)
                        };
                    }

                    candidate = candidate with
                    {
                        PackageHashes = EmulatorPackages.PreserveArchives(downloads, candidate.PackageCachePath)
                    };
                    if (ownsData)
                    {
                        Directory.CreateDirectory(data);
                    }

                    ConfigureData(candidate);
                    EmulatorPortable.Verify(candidate);

                    candidate = EmulatorPrerequisites.RefreshState(candidate);
                    Mutate(store => store with
                    {
                        Installations = [.. store.Installations.Where(item => item.Id != id), candidate]
                    });
                    published = true;
                    if (Directory.Exists(BiosFolder))
                    {
                        RelinkBios(cancellationToken, installationId: id);
                    }
                }
                catch
                {
                    if (published)
                    {
                        Mutate(store => store with
                        {
                            Installations = previous is null
                                ? store.Installations.Where(item => item.Id != id).ToArray()
                                : [.. store.Installations.Where(item => item.Id != id), previous]
                        });
                        if (previous is not null)
                        {
                            ConfigureData(previous);
                        }
                    }

                    EmulatorPackages.DeleteOwned(versionRoot, installRoot);
                    throw;
                }

                PruneVersions(candidate, cancellationToken);
            }), cancellationToken).ConfigureAwait(false);
            SetStatus(definition.Name + " " + release.Version + " installed"
                      + (cores.Length > 0 ? $" with all {cores.Length} published core packages." : ".")
                      + " Configure locally supplied firmware/keys where required.");
        }
        finally
        {
            try
            {
                EmulatorPackages.DeleteOwned(stage, installRoot);
            }
            catch (IOException ex)
            {
                Log.Warn("Emulator staging cleanup remains pending: " + ex.Message);
            }
        }
    }

    private async Task RegisterExternalAsync(EmulatorPackageDefinition definition, string executable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) ||
            !definition.ExecutableNames.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose the expected " + definition.Name + " executable.");
        }

        var architecture = PeArchitecture(executable);
        if (!definition.Architectures.Contains(architecture, StringComparer.Ordinal))
        {
            throw new InvalidDataException("This external emulator architecture is not supported.");
        }

        var id = ExternalId(definition.Id, executable);
        var directory = Path.GetDirectoryName(executable)!;
        var externalData = EmulatorPortable.ReadExternalData(definition.Id, directory);
        var args = definition.DataPolicy.DataArguments.Concat(definition.LaunchArguments).ToArray();
        var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "External";
        EmulatorInstallation installed = new()
        {
            Id = id, DefinitionId = definition.Id, Name = definition.Name, Managed = false,
            Channel = definition.Sources.Keys.First(), Architecture = architecture, Version = version,
            Root = directory, ExecutablePath = executable, DataPath = externalData, OwnsData = false,
            Source = "External", Systems = definition.Systems, LaunchArguments = [.. args],
            DataPolicy = definition.DataPolicy,
            Cores = definition.DataPolicy.HasCores && Directory.Exists(Path.Combine(directory, "cores"))
                ? Directory.EnumerateFiles(Path.Combine(directory, "cores"), "*.dll").Select(path => new EmulatorCore
                {
                    Id = Path.GetFileNameWithoutExtension(path), Name = Path.GetFileNameWithoutExtension(path),
                    Path = path, MetadataMissing = true
                }).ToArray()
                : []
        };
        EmulatorPortable.Verify(installed);
        installed = EmulatorPrerequisites.RefreshState(installed);
        await Task.Run(() => Mutate(store => store with
        {
            Installations = [.. store.Installations.Where(item => item.Id != id), installed],
            ForgottenExternalIds = store.ForgottenExternalIds.Where(item => item != id).ToArray()
        }), cancellationToken).ConfigureAwait(false);
        SetStatus("Using external " + definition.Name + ". WSGM does not own its updates or data.");
    }

    private async Task DiscoverExternalAsync(CancellationToken cancellationToken)
    {
        var state = EmulatorStorage.ReadStore(_context.Root);
        var installedIds = state.Installations.Select(item => item.DefinitionId).ToHashSet(StringComparer.Ordinal);
        var forgotten = state.ForgottenExternalIds.ToHashSet(StringComparer.Ordinal);
        foreach (var definition in _catalog.Definitions)
        {
            if (installedIds.Contains(definition.Id))
            {
                continue;
            }

            foreach (var executable in definition.ExecutableNames)
            {
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs",
                        definition.Name, executable),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "emudeck",
                        "Emulators", definition.Id, executable),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps",
                        definition.Id, "current", executable)
                }.ToList();
                foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    using var key =
                        hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executable);
                    if (key?.GetValue(null) is string path)
                    {
                        candidates.Add(path.Trim('"'));
                    }
                }

                foreach (var found in candidates.Where(path =>
                             File.Exists(path) && !forgotten.Contains(ExternalId(definition.Id, path))))
                {
                    try
                    {
                        await RegisterExternalAsync(definition, found, cancellationToken).ConfigureAwait(false);
                        installedIds.Add(definition.Id);
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException
                                                   or InvalidOperationException or UnauthorizedAccessException)
                    {
                        SetStatus("External " + definition.Name + " could not be used: " + ex.Message);
                    }
                }

                if (installedIds.Contains(definition.Id))
                {
                    break;
                }
            }
        }
    }

    private static string ExternalId(string definitionId, string executable)
    {
        return definitionId + "-external-"
                            + Convert.ToHexString(
                                SHA256.HashData(
                                    Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToUpperInvariant())))[..20];
    }

    internal static void ConfigureData(EmulatorInstallation installed)
    {
        if (!installed.Managed)
        {
            return;
        }

        if (installed.DataPolicy.ConfigPaths.Count == 0)
        {
            return;
        }

        var root = Path.GetDirectoryName(installed.ExecutablePath)!;
        var fields = installed.DataPolicy.ConfigPaths.ToDictionary(pair => pair.Key, pair =>
            pair.Value.Replace("{program}", root, StringComparison.Ordinal)
                .Replace("{data}", installed.DataPath, StringComparison.Ordinal));
        var configPath = Path.Combine(installed.DataPath, installed.DataPolicy.ConfigFile);
        if (fields.Any(pair =>
                IniFile.ReadValue(configPath, pair.Key)?.Trim('"').Replace('/', Path.DirectorySeparatorChar) !=
                pair.Value) ||
            (installed.DefinitionId == "retroarch" &&
             EmulatorPortable.RetroArchFlags.Any(key => IniFile.ReadValue(configPath, key) != "false")))
        {
            EmulatorPrerequisites.EnsureStopped(installed);
        }

        foreach (var (key, value) in fields)
        {
            var fileSetting = key is "core_options_path" or "content_history_path" or "content_favorites_path"
                or "content_image_history_path" or "content_music_history_path" or "content_video_history_path";
            Directory.CreateDirectory(fileSetting ? Path.GetDirectoryName(value)! : value);
        }

        IniFile.SetValues(Path.Combine(installed.DataPath, installed.DataPolicy.ConfigFile), fields.ToDictionary(
            pair => pair.Key, pair => string.Concat((char)34,
                pair.Value.Replace(Path.DirectorySeparatorChar, '/'), (char)34)));
        if (installed.DefinitionId == "retroarch")
        {
            IniFile.SetValues(Path.Combine(installed.DataPath, installed.DataPolicy.ConfigFile),
                EmulatorPortable.RetroArchFlags.ToDictionary(key => key, _ => "false"));
        }
    }

    private void Admission(CancellationToken cancellationToken, Action action)
    {
        using var lease = EmulatorStorage.AcquireGate(_context.Root, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        action();
    }

    private void Mutate(Func<EmulatorStore, EmulatorStore> mutation, bool publishSemantics = true)
    {
        using var lease = EmulatorStorage.AcquireGate(_context.Root, CancellationToken.None);
        var current = EmulatorStorage.ReadStore(_context.Root);
        var changed = mutation(current);
        var semanticChanged = !current.Installations.SequenceEqual(changed.Installations)
                              || !current.SystemPreferences.SequenceEqual(changed.SystemPreferences);
        Directory.CreateDirectory(_context.Root);
        AtomicFile.WriteText(EmulatorStorage.StorePath(_context.Root),
            JsonSerializer.Serialize(changed, EmulatorStorage.JsonOptions), true);
        lock (_stateLock)
        {
            _state = changed;
        }

        NotifyChanged();
        if (semanticChanged && publishSemantics)
        {
            NotifyInstallationsChanged();
        }
    }

    private void NotifyInstallationsChanged()
    {
        if (!_disposed)
        {
            try
            {
                InstallationsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warn("Emulator semantic observer failed: " + ex.Message);
            }
        }
    }

    private async Task OperationAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await Initialization.WaitAsync(active.Token).ConfigureAwait(false);
        await _operations.WaitAsync(active.Token).ConfigureAwait(false);
        lock (_stateLock)
        {
            _active = active;
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NotifyChanged();
            await Task.Run(async () => { await operation(active.Token).ConfigureAwait(false); }, active.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested)
        {
            SetStatus("Emulator operation cancelled. The active version is preserved.");
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            throw;
        }
        finally
        {
            lock (_stateLock)
            {
                _active = null;
            }

            _operations.Release();
            NotifyChanged();
        }
    }

    internal void SetStatus(string status)
    {
        lock (_stateLock)
        {
            _status = status;
        }

        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A presentation observer must not turn a committed install into a failed transaction.
            Log.Warn("Emulator state observer failed: " + ex.Message);
        }
    }

    private void RecoverTransactions(CancellationToken cancellationToken)
    {
        using var admission = EmulatorStorage.TryAcquireGate(_context.Root, TimeSpan.Zero);
        if (admission is null)
        {
            return;
        }

        var installations = EmulatorStorage.ReadInstallations(_context.Root).Where(item => item.Managed).ToArray();
        if (Directory.Exists(ProgramRoot))
        {
            foreach (var root in Directory.EnumerateDirectories(ProgramRoot))
            {
                if (!installations.Any(installed => installed.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
                {
                    EmulatorPortableSetup.CopyData(root,
                        Path.Combine(DataRoot, Path.GetFileName(root), ".portable-snapshots", "removed-installation"),
                        cancellationToken);
                    DeleteRetired(root, ProgramRoot);
                    continue;
                }

                foreach (var stage in Directory.EnumerateDirectories(root, "staging-*"))
                {
                    DeleteRetired(stage, ProgramRoot);
                }
            }
        }

        foreach (var installed in installations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var definition = Definition(installed.DefinitionId);
                var normalized = Pcsx2Data.Normalize(installed) with
                {
                    DataPolicy = definition.DataPolicy,
                    LaunchArguments = [.. definition.DataPolicy.DataArguments, .. definition.LaunchArguments],
                    Environment = new Dictionary<string, string>(definition.DataPolicy.Environment)
                };
                var portable = true;
                try
                {
                    EmulatorPortable.Verify(normalized);
                }
                catch (InvalidDataException)
                {
                    portable = false;
                }

                if (!portable)
                {
                    PreserveLegacyRpcData(installed, Path.Combine(DataRoot, installed.Id), cancellationToken);
                    normalized = EmulatorPortableSetup.Prepare(normalized, Path.Combine(DataRoot, installed.Id),
                        installed, cancellationToken, false, () => KnownRomPaths(installed));
                    normalized = normalized with
                    {
                        ConfiguredPrerequisites = installed.ConfiguredPrerequisites.ToDictionary(pair => pair.Key,
                            pair => Path.IsPathFullyQualified(pair.Value) &&
                                    StoragePaths.IsUnder(installed.DataPath, pair.Value)
                                ? Path.Combine(normalized.DataPath,
                                    Path.GetRelativePath(installed.DataPath, pair.Value))
                                : pair.Value)
                    };
                }

                ConfigureData(normalized);
                EmulatorPortable.Verify(normalized);
                if (JsonSerializer.Serialize(normalized, EmulatorStorage.JsonOptions) !=
                    JsonSerializer.Serialize(installed, EmulatorStorage.JsonOptions))
                {
                    Mutate(store => store with
                    {
                        Installations = store.Installations.Select(item => item.Id == installed.Id ? normalized : item)
                            .ToArray()
                    });
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or InvalidOperationException)
            {
                SetStatus("Emulator data binding could not be repaired: " + exception.Message);
            }

            PruneVersions(installed, cancellationToken);
        }
    }

    internal void PruneVersions(EmulatorInstallation installed, CancellationToken token)
    {
        var versions = Path.Combine(installed.Root, "versions");
        if (!Directory.Exists(versions))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(versions))
        {
            if (StoragePaths.IsUnder(directory, installed.ExecutablePath)
                || (installed.PreviousExecutablePath.Length > 0 &&
                    StoragePaths.IsUnder(directory, installed.PreviousExecutablePath)))
            {
                continue;
            }

            EmulatorPortableSetup.PreserveVersion(installed, directory, Path.Combine(DataRoot, installed.Id), token);
            DeleteRetired(directory, installed.Root);
        }
    }

    private void PreserveVersions(EmulatorInstallation installed, CancellationToken token)
    {
        var retained = Path.Combine(DataRoot, installed.Id);
        EmulatorPortableSetup.CopyData(installed.DataPath, retained, token);
        var versions = Path.Combine(installed.Root, "versions");
        if (!Directory.Exists(versions))
        {
            return;
        }

        foreach (var version in Directory.EnumerateDirectories(versions))
        {
            EmulatorPortableSetup.PreserveVersion(installed, version, retained, token);
        }
    }

    private void PreserveLegacyRpcData(EmulatorInstallation installed, string retained, CancellationToken token)
    {
        if (installed.DefinitionId != "rpcs3" ||
            Directory.Exists(Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!, "portable")) ||
            !installed.Environment.TryGetValue("RPCS3_CONFIG_DIR", out var setting))
        {
            return;
        }

        var expanded = setting.Replace("{data}", installed.DataPath, StringComparison.Ordinal);
        var actual = Path.EndsInDirectorySeparator(expanded) ? expanded : Path.GetDirectoryName(expanded)!;
        if (!Directory.Exists(actual) || !StoragePaths.IsUnder(DataRoot, actual) ||
            !(File.Exists(Path.Combine(actual, "config.yml")) || Directory.Exists(Path.Combine(actual, "dev_hdd0"))))
        {
            return;
        }

        var otherRoots = EmulatorStorage.ReadInstallations(_context.Root)
            .Where(item => !string.Equals(item.DataPath, actual, StringComparison.OrdinalIgnoreCase) &&
                           StoragePaths.IsUnder(actual, item.DataPath))
            .Select(item => Path.GetRelativePath(actual, item.DataPath).Split(Path.DirectorySeparatorChar)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        EmulatorPortableSetup.CopyData(actual, retained, token, excludedNames: otherRoots);
    }

    private static void DeleteRetired(string path, string root)
    {
        try
        {
            EmulatorPackages.DeleteOwned(path, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Emulator files remain for later cleanup: " + ex.Message);
        }
    }

    private EmulatorPackageDefinition Definition(string id)
    {
        return _catalog.Definitions.SingleOrDefault(item => item.Id == id)
               ?? throw new InvalidOperationException("Unknown emulator definition.");
    }

    private EmulatorInstallation Installation(string id)
    {
        return EmulatorStorage.ReadInstallations(_context.Root)
                   .SingleOrDefault(item => item.Id == id) ??
               throw new InvalidOperationException("This emulator is not installed.");
    }

    private static void RequireManaged(EmulatorInstallation installed)
    {
        if (!installed.Managed)
        {
            throw new InvalidOperationException("This emulator is external. WSGM does not own its update or removal.");
        }
    }

    private static string Architecture(EmulatorPackageDefinition definition)
    {
        var architecture = RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "arm64"
            : "x64";
        return definition.Architectures.Contains(architecture, StringComparer.Ordinal)
            ? architecture
            : throw new InvalidOperationException(definition.Name +
                                                  " does not publish a supported native package for this Windows architecture.");
    }

    private static string PeArchitecture(string path)
    {
        using var stream = File.OpenRead(path);
        using PEReader reader = new(stream);
        return reader.PEHeaders.CoffHeader.Machine switch
        {
            Machine.Amd64 => "x64",
            Machine.Arm64 => "arm64",
            _ => throw new InvalidDataException("This Windows emulator architecture is unsupported.")
        };
    }
}
