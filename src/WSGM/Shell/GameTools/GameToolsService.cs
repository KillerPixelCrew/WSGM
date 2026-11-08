using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Runs Game Tools for both interfaces: scanning a game's folder, publishing its state, and running install,
///     update, repair, configure and remove transactions one at a time.
/// </summary>
/// <remarks>
///     <para>
///         Transaction policy lives in <c>Core/GameTools</c>: <see cref="GameToolAdmission" /> decides whether a
///         request may start, <see cref="GameToolInstaller" /> runs it, <see cref="GameToolRecovery" /> undoes what
///         a crash interrupted and <see cref="GameRunningCheck" /> decides whether the game is closed. This service
///         supplies Steam's and the session's answers, serializes the work and publishes the results.
///     </para>
///     <para>
///         The install folder and shortcut target are read from Steam on every scan; an absolute path is
///         never kept as truth. The durable record keeps paths relative to the install root, so a game moved
///         to another library is rebased on the next scan.
///     </para>
///     <para>
///         Commands return as soon as work is admitted, because the Steam bridge must answer promptly. The
///         outcome is published through <see cref="Changed" /> as the game's <see cref="GameToolsState.Notice" />
///         or <see cref="GameToolsState.Error" />. Progress is throttled.
///     </para>
///     <para>
///         A scan walks the folder once; the state built from it is composed outside the lock and swapped in,
///         and <see cref="Peek" /> only adds the live progress, loading and message fields. Configure, remove
///         and the rescan after an operation reuse the cached walk, and opening a page again within a few
///         seconds of a scan does not walk again. Only the explicit Refresh always walks.
///     </para>
/// </remarks>
internal sealed class GameToolsService : IGameToolsSource, IDisposable
{
    private const long ProgressInterval = 250;

    // The cog menu opens the page and the page then opens itself; one walk serves both.
    private const long FreshScan = 5000;
    private readonly PeArchitectureCache _architectures = new();
    private readonly HashSet<string> _checking = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly GameToolInstaller _installer;
    private readonly GameToolSharedPlugins _sharedPlugins;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<uint> _loading = [];
    private readonly Dictionary<uint, (string? Error, string? Notice)> _messages = [];
    private readonly GameToolRecovery _recovery;
    private readonly HashSet<uint> _releaseStale = [];
    private readonly Lazy<GameToolReleases> _releases;
    private readonly GameRunningCheck _running;
    private readonly Dictionary<uint, GameToolsScan> _scans = [];
    private readonly Dictionary<uint, GameToolsState> _states = [];
    private readonly Func<SteamClient?> _steam;
    private readonly object _sync = new();

    private readonly string _userRoot;
    private readonly Lazy<IReadOnlyList<string>> _vendors;
    private uint _appId;
    private GameToolsBusy? _busy;
    private bool _disposed;
    private long _lastProgress;

    // When the running operation entered its current phase, so each phase's duration reaches the log.
    private long _phaseStarted;
    private GameToolsLibrary? _library;
    private bool _libraryDirty;
    private Task<GameToolsLibrary>? _libraryRead;
    private Task? _operation;
    private CancellationTokenSource? _operationCancellation;
    private uint _viewedAppId;

    /// <summary>Creates the service; the catalog and the release owner are created on first use.</summary>
    /// <param name="context">The per-user data location.</param>
    /// <param name="steam">The session's Steam client, or null while it is unavailable.</param>
    /// <param name="appRunning">Whether Steam reports an AppID running; null when unknown.</param>
    /// <param name="handler">An optional HTTP handler, the test seam.</param>
    /// <param name="vendors">Detects GPU vendors; defaults to the GPU driver library.</param>
    internal GameToolsService(UserDataContext context, Func<SteamClient?> steam, Func<uint, bool?> appRunning,
        HttpMessageHandler? handler = null, Func<IReadOnlyList<GpuVendor>>? vendors = null)
    {
        _userRoot = context.Root;
        _steam = steam;
        _running = new GameRunningCheck(new SessionProcessProbe(appRunning));
        _releases = new Lazy<GameToolReleases>(() => new GameToolReleases(_userRoot, handler));
        _installer = new GameToolInstaller(_userRoot);
        _sharedPlugins = new GameToolSharedPlugins(_userRoot);
        _recovery = new GameToolRecovery(_userRoot, _installer, _running);
        var detect = vendors ?? GpuDriver.DetectVendors;
        _vendors = new Lazy<IReadOnlyList<string>>(() => DetectVendors(detect));
    }

    private static GameToolCatalog Catalog => GameToolCatalog.Current;

    private GameToolReleases Releases => _releases.Value;

    /// <inheritdoc />
    public void Dispose()
    {
        Task operation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            operation = _operation ?? Task.CompletedTask;
        }

        _lifetime.Cancel();
        operation.ContinueWith(_ =>
        {
            if (_releases.IsValueCreated)
            {
                _releases.Value.Dispose();
            }

            _lifetime.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ObserveFaults();
    }

    /// <summary>Raised after state, progress or a result changed, on whatever thread did the work.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RefreshAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartRefresh(appId);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public GameToolsState Peek(uint appId)
    {
        lock (_sync)
        {
            var messages = _messages.GetValueOrDefault(appId);
            var busy = _busy?.AppId == appId ? _busy : null;
            var loading = _loading.Contains(appId);
            if (_states.TryGetValue(appId, out var state))
            {
                return state with
                {
                    Busy = busy,
                    Loading = loading,
                    Error = messages.Error,
                    Notice = messages.Notice,
                    Tools = _checking.Count == 0 ? state.Tools : [.. state.Tools.Select(WithChecking)]
                };
            }

            return Placeholder(appId) with
            {
                Busy = busy, Loading = true, Error = messages.Error, Notice = messages.Notice
            };
        }
    }

    /// <inheritdoc />
    public GameToolsLibrary PeekLibrary()
    {
        lock (_sync)
        {
            if (_library is null && !_disposed)
            {
                QueueLibraryLocked();
            }

            return _library ?? GameToolsLibrary.Pending;
        }
    }

    /// <inheritdoc />
    public Task<GameToolsLibrary> ReadLibraryAsync(CancellationToken cancellationToken = default)
    {
        Task<GameToolsLibrary> pending;
        lock (_sync)
        {
            if (_disposed)
            {
                return Task.FromResult(_library ?? GameToolsLibrary.Pending);
            }

            pending = _libraryRead ??= Task.Run(BuildLibrary, CancellationToken.None);
        }

        return pending.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GameToolsState> ReadAsync(uint appId, CancellationToken cancellationToken = default)
    {
        if (appId == 0)
        {
            return Placeholder(0) with
            {
                Loading = false, Availability = "notInstalled", Reason = "No game was supplied."
            };
        }

        bool queue;
        lock (_sync)
        {
            if (_disposed)
            {
                return Peek(appId);
            }

            _viewedAppId = appId;

            // An operation holds the gate until it finishes, so a game it doesn't rescan is queued behind it
            // and published through Changed instead of waiting here for the whole download.
            queue = _operation is { IsCompleted: false };
            if (queue && _busy?.AppId == appId && _states.ContainsKey(appId))
            {
                return Peek(appId);
            }
        }

        if (queue)
        {
            StartRefresh(appId);
            return Peek(appId);
        }

        if (!IsFresh(appId))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ScanAsync(appId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"Game Tools: scan of {appId} failed", ex);
                RecordScanFailure(appId, ex);
            }
            finally
            {
                _gate.Release();
            }
        }

        StartLatestLookups(appId, false);
        Changed?.Invoke();
        return Peek(appId);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CheckUpdatesAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetMessage(appId, null, null);
        StartLatestLookups(appId, true);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ChooseRuntimeAsync(uint appId, string path,
        CancellationToken cancellationToken)
    {
        if (IsBusy())
        {
            return new SteamUiCommandResult(false, "Wait for the current Game Tools change to finish.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The choice only needs the install root and the record; the page's walk is reused.
            var scan = await ScanAsync(appId, CancellationToken.None, true).ConfigureAwait(false);
            if (GameToolAdmission.Folder(scan.InstallFolder.Length > 0 && scan.HasFolder, scan.Reason) is { } missing)
            {
                return Fail(appId, missing);
            }

            if (GameToolAdmission.RuntimeChoice(scan.InstallFolder, path, scan.Record, Catalog, out var choice) is
                { } refused)
            {
                return Fail(appId, refused);
            }

            var runtime = choice!;
            GameToolStorage.RememberRuntime(_userRoot, appId, scan.IsShortcut, scan.Name, runtime);
            Log.Info($"Game Tools: runtime for {appId} set to {runtime.RelativePath}.");
            await ScanAsync(appId, CancellationToken.None, true).ConfigureAwait(false);
            SetMessage(appId, null, "WSGM will use " + Path.GetFileName(runtime.RelativePath) + ".");
            return SteamUiCommandResult.Applied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"Game Tools: could not remember the runtime for {appId}: {ex.Message}");
            return Fail(appId, "WSGM couldn't save the choice: " + ex.Message);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> InstallAsync(GameToolsInstallRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(request.AppId, request.ToolId, request.ReplaceExisting ? "replaceExisting" : "install",
            tool => GameToolAdmission.Settings(tool, request.Settings)
                    ?? (request.Channel is { } channel && tool.Channel(channel) is null
                        ? "Unknown release channel."
                        : null)
                    ?? (request.Addons?.Keys.Any(id => tool.Addon(id) is null) == true ? "Unknown add-on." : null),
            (tool, context, token) => InstallCoreAsync(tool, request, context, token));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> UpdateAsync(uint appId, string toolId, bool replaceConfig,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(appId, toolId, "update", null, (tool, context, token) => ReinstallCoreAsync(tool, appId, "update",
            async (manifest, releaseToken) =>
            {
                var channel = tool.ChannelOrDefault(manifest.Channel);
                return (await LatestReleaseAsync(tool, channel, releaseToken).ConfigureAwait(false), channel);
            }, replaceConfig, context, token));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SwitchChannelAsync(uint appId, string toolId, string channel,
        bool replaceConfig, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(appId, toolId, "switchChannel",
            tool => tool.Channel(channel) is null ? "Unknown release channel." : null,
            (tool, context, token) => ReinstallCoreAsync(tool, appId, "switchChannel", async (manifest, releaseToken) =>
            {
                var target = tool.Channel(channel)!;
                if (tool.ChannelOrDefault(manifest.Channel) == target)
                {
                    throw new GameToolException(tool.Name + " already follows the " + target.Label.ToLowerInvariant()
                                                + " channel. Use Update instead.");
                }

                // The other channel's latest release is installed whether it is newer or older.
                return (await LatestReleaseAsync(tool, target, releaseToken).ConfigureAwait(false), target);
            }, replaceConfig, context, token));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RepairAsync(uint appId, string toolId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(appId, toolId, "repair", null, (tool, context, token) => ReinstallCoreAsync(tool, appId, "repair",
            async (manifest, ct) => (manifest.Adopted && manifest.AssetSha256.Length == 0
                ? await LatestReleaseAsync(tool, tool.ChannelOrDefault(manifest.Channel), ct).ConfigureAwait(false)
                : GameToolReleases.FromManifest(manifest), tool.ChannelOrDefault(manifest.Channel)), false, context, token));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> AdoptAsync(GameToolsInstallRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(request.AppId, request.ToolId, "adopt", tool => GameToolAdmission.Settings(tool, request.Settings),
            async (tool, context, token) =>
            {
                if (!tool.UsesGraphicsApi)
                {
                    throw new GameToolException("This tool does not declare an adoptable native file set.");
                }

                var scan = await ScanAsync(request.AppId, token).ConfigureAwait(false);
                tool = tool.ForGame(scan.IsShortcut);
                var target = GameToolAdmission.RequireInstallable(tool, request.AppId, scan.InstallFolder, scan.Availability,
                    scan.Reason, scan.Record, scan.Resolution, scan.AntiCheat, request.AcknowledgeRisk);
                await RequireClosedAsync(target).ConfigureAwait(false);
                var inspection = GameToolInstaller.Inspect(tool, target.TargetDirectory, null);
                var modes = tool.InstallModes.Where(mode => mode.Architectures.Contains(target.Architecture)
                    && inspection.Unmanaged.Any(copy => copy.RelativePath.Equals(mode.Proxy, StringComparison.OrdinalIgnoreCase))).ToArray();
                var proxy = request.ProxyName;
                if (proxy is null && scan.GraphicsApis.Length <= 1)
                {
                    var matching = modes.Where(mode => mode.Apis.Intersect(scan.GraphicsApis).Any()).ToArray();
                    var choices = matching.Length > 0 ? matching : modes;
                    if (choices.Length == 1 || (scan.GraphicsApis.Length == 1 && choices.Length > 0))
                    {
                        proxy = choices.OrderByDescending(mode => mode.Priority).First().Proxy;
                    }
                }
                if (proxy is null || modes.All(mode => mode.Proxy != proxy))
                {
                    throw new GameToolException("Choose the existing loader DLL and its pipeline before managing this installation.");
                }

                var mode = tool.InstallMode(proxy);
                var scratch = Path.Combine(Path.GetTempPath(), "WSGM-adopt-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                try
                {
                    foreach (var copy in inspection.Unmanaged)
                    {
                        var name = copy.RelativePath.Equals(proxy, StringComparison.OrdinalIgnoreCase) ? tool.PrimaryFile : copy.RelativePath;
                        var path = Path.Combine(scratch, name);
                        File.Copy(Path.Combine(target.TargetDirectory, copy.RelativePath), path);
                        var identity = GameToolBinaryIdentification.Read(path);
                        if (identity.ToolId != tool.Id || identity.Architecture != target.Architecture)
                        {
                            throw new GameToolException("An existing DLL changed identity or architecture during adoption; nothing was adopted.");
                        }
                    }

                    var hashes = Directory.EnumerateFiles(scratch).ToDictionary(path => Path.GetFileName(path)!,
                        path => (GameToolInstaller.Sha256(path), new FileInfo(path).Length), StringComparer.OrdinalIgnoreCase);
                    var release = new HostedRelease("", "local-files", "", "Local", new ReleaseAsset("local-files", ""));
                    var package = new GameToolPackage(release, scratch, "local", "", target.Architecture) { Hashes = hashes! };
                    package = await WithSharedPluginAsync(tool, mode, request.AppId, package, context, token, request.Settings).ConfigureAwait(false);
                    var configuration = await PrepareWorkingConfigAsync(tool, request.AppId, target, request.Settings).ConfigureAwait(false);
                    package = package with { SharedSettingsDirectory = configuration.Directory.Length > 0
                        ? configuration.Directory : package.SharedSettingsDirectory };
                    var result = await _installer.InstallAsync(configuration.Tool, target, package,
                        new GameToolRequest("adopt", proxy, configuration.Settings), null, () => _running.ClosedMessage(target),
                        phase => context.Phase(phase, false), token, inspection).ConfigureAwait(false);
                    GameToolStorage.RememberIdentity(_userRoot, request.AppId, scan.IsShortcut, scan.Name);
                    return "Existing " + tool.Name + " DLLs are now managed. Repair installs the latest verified upstream version. " + result.Notice;
                }
                finally
                {
                    GameToolStorage.DeleteTree(scratch);
                }
            });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ConfigureAsync(uint appId, string toolId, string? proxyName,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(appId, toolId, "configure",
            tool => GameToolAdmission.Configure(tool, proxyName, settings),
            async (tool, context, _) =>
            {
                var (target, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
                await RequireClosedAsync(target).ConfigureAwait(false);
                context.Phase(GameToolPhase.Settings, false);
                if (!manifest.Enabled && proxyName is not null)
                {
                    throw new GameToolException("Enable the tool before changing its configuration.");
                }

                if (tool.UsesGraphicsApi && proxyName is not null && proxyName != manifest.ProxyName)
                {
                    throw new GameToolException("Remove and reinstall the tool to change its graphics API.");
                }

                var shared = settings.Where(pair => tool.Settings.First(setting => setting.Id == pair.Key).Scope == "shared")
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (shared.Count > 0)
                {
                    var folder = Path.Combine(SpecialKPluginLocation.Resolve(create: true), "Global");
                    _sharedPlugins.Register(folder, appId);
                    await _sharedPlugins.ConfigureAsync(folder, tool.SharedSettingsFile, tool, shared, target.Architecture,
                        () => SharedPluginClosed(folder)).ConfigureAwait(false);
                }

                var local = settings.Where(pair => !shared.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
                var configuration = await PrepareWorkingConfigAsync(tool, appId, target, local).ConfigureAwait(false);
                tool = configuration.Tool;
                if (configuration.Directory.Length > 0)
                {
                    manifest = manifest with { SharedSettingsDirectory = configuration.Directory };
                }

                var outcome = await Task.Run(() => _installer.Configure(tool, target, manifest, proxyName, configuration.Settings,
                    () => _running.ClosedMessage(target)), CancellationToken.None).ConfigureAwait(false);
                return shared.Count > 0 ? "Special K settings saved; shared controls apply to all Special K games on their next start. " + outcome.Notice : outcome.Notice;
            });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetEnabledAsync(uint appId, string toolId, bool enabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(appId, toolId, enabled ? "enable" : "disable", null, async (tool, context, _) =>
        {
            var (target, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
            await RequireClosedAsync(target).ConfigureAwait(false);
            context.Phase(GameToolPhase.Settings, false);
            return await Task.Run(() => _installer.SetEnabled(tool, target, manifest, enabled,
                () => _running.ClosedMessage(target)), CancellationToken.None).ConfigureAwait(false);
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetAddonAsync(uint appId, string toolId, string addonId, bool enabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = enabled ? "addAddon" : "removeAddon";
        return Start(appId, toolId, operation, tool => tool.Addon(addonId) is null ? "Unknown add-on." : null,
            async (tool, context, token) =>
            {
                var addon = tool.Addon(addonId)!;
                var (target, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
                GameToolAdmission.RequireCompleted(manifest);
                var installed = manifest.Addons.Any(record => record.Id == addon.Id);
                if (!enabled && !installed)
                {
                    throw new GameToolException(addon.Name + " isn't installed.");
                }

                if (enabled && !installed)
                {
                    // Turning an add-on on is only offered for a game on its list; the list is checked again here.
                    var support = await Releases.SupportAsync(tool, addon, false, token).ConfigureAwait(false);
                    var program = Path.GetFileName(manifest.RuntimeExecutable);
                    if (!support.Supports(program))
                    {
                        throw new GameToolException(support.Error ?? addon.Name + " doesn't support " + program + ".");
                    }
                }

                await RequireClosedAsync(target).ConfigureAwait(false);
                var channel = tool.ChannelOrDefault(manifest.Channel);
                var package = await Releases.PrepareAsync(tool, channel, GameToolReleases.FromManifest(manifest),
                    context.Progress, token, target.Architecture, manifest.ProxyName).ConfigureAwait(false);
                IEnumerable<GameToolAddon> addons = manifest.Addons.Select(record => tool.Addon(record.Id))
                    .OfType<GameToolAddon>().Where(item => item.Id != addon.Id);
                package = await WithAddonsAsync(tool, package, enabled ? addons.Append(addon) : addons, manifest,
                    target.Architecture, context, token).ConfigureAwait(false);
                context.Phase(GameToolPhase.Copy, false);
                var outcome = await _installer.InstallAsync(tool, target, package,
                        new GameToolRequest(operation, manifest.ProxyName,
                            new Dictionary<string, string>(StringComparer.Ordinal), false, channel.Id),
                        manifest, () => _running.ClosedMessage(target), phase => context.Phase(phase, false), token)
                    .ConfigureAwait(false);
                return outcome.Notice;
            }, tool => tool.Addon(addonId)?.Name);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RemoveAsync(uint appId, string toolId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(appId, toolId, "remove", null, async (tool, context, _) =>
        {
            var (target, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
            await RequireClosedAsync(target).ConfigureAwait(false);
            context.Phase(GameToolPhase.Remove, false);
            var outcome = await Task.Run(() => _installer.Remove(tool, target, manifest), CancellationToken.None)
                .ConfigureAwait(false);
            return outcome.Notice;
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CancelAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_busy is not { } busy || busy.AppId != appId || _operationCancellation is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Nothing is running for this game."));
            }

            if (!busy.Cancellable)
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "Files are being written; this step finishes or rolls back on its own."));
            }

            _operationCancellation.Cancel();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Opens the page for a game: makes it the Steam publication's game and starts a scan.</summary>
    /// <param name="appId">The game from the cog menu.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Applied when the game was accepted; the scan result follows through <see cref="Changed" />.</returns>
    internal Task<SteamUiCommandResult> OpenAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "No game was supplied."));
        }

        _appId = appId;
        SetMessage(appId, null, null);
        if (IsFresh(appId))
        {
            Changed?.Invoke();
            StartLatestLookups(appId, false);
        }
        else
        {
            StartRefresh(appId);
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>The Steam publication: the game opened from the cog menu.</summary>
    /// <returns>The state, or null when no game was opened.</returns>
    internal ValueTask<GameToolsState?> ReadStateAsync()
    {
        var appId = _appId;
        return new ValueTask<GameToolsState?>(appId == 0 ? null : Peek(appId));
    }

    /// <summary>The Steam library publication: every game with tools, read without a folder walk.</summary>
    /// <returns>The cached list; the first call starts its read.</returns>
    internal ValueTask<GameToolsLibrary?> ReadLibraryStateAsync()
    {
        return new ValueTask<GameToolsLibrary?>(PeekLibrary());
    }

    /// <summary>Reads the game list again, for a page that opens it.</summary>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Applied; the list follows through <see cref="Changed" />.</returns>
    internal Task<SteamUiCommandResult> RefreshLibraryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueueLibrary();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private async Task<string> InstallCoreAsync(GameToolDefinition tool, GameToolsInstallRequest request,
        OperationContext context, CancellationToken token)
    {
        var appId = request.AppId;
        var scan = await ScanAsync(appId, token).ConfigureAwait(false);
        tool = tool.ForGame(scan.IsShortcut);
        var target = GameToolAdmission.RequireInstallable(tool, appId, scan.InstallFolder, scan.Availability,
            scan.Reason, scan.Record, scan.Resolution, scan.AntiCheat, request.AcknowledgeRisk);
        await RequireClosedAsync(target).ConfigureAwait(false);
        var channel = tool.ChannelOrDefault(request.Channel);
        var release = await LatestReleaseAsync(tool, channel, token).ConfigureAwait(false);
        context.Release(channel, release);
        var inspection = await Task.Run(() => GameToolInstaller.Inspect(tool, target.TargetDirectory, null), token)
            .ConfigureAwait(false);
        var proxy = request.ProxyName ?? GameToolInstaller.SuggestProxy(inspection.Proxies, tool.Proxy.Default,
            tool.UsesGraphicsApi ? tool : null, scan.GraphicsApis, target.Architecture, scan.GraphicsImports);
        if (proxy.Length == 0)
        {
            throw new GameToolException(scan.GraphicsApis.Length > 1
                ? "Several render pipelines were detected. Choose the pipeline the game will use."
                : "No free proxy matches the detected graphics API and architecture. Choose a supported file set explicitly.");
        }

        var mode = tool.InstallMode(proxy);
        if (mode.RequiredImports.Any(name => !scan.GraphicsImports.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            throw new GameToolException("This loader requires the game to import " + string.Join(", ", mode.RequiredImports) + ".");
        }

        var package = await Releases.PrepareAsync(tool, channel, release, context.Progress, token,
                target.Architecture, proxy)
            .ConfigureAwait(false);
        package = await WithSharedPluginAsync(tool, mode, appId, package, context, token, request.Settings).ConfigureAwait(false);
        package = await WithAddonsAsync(tool, package,
            await InstallAddonsAsync(tool, request, Path.GetFileName(target.RuntimeExecutable), token)
                .ConfigureAwait(false), null, target.Architecture, context, token).ConfigureAwait(false);
        context.Phase(GameToolPhase.Copy, false);

        // Reinspect after downloading. The transaction refuses a collision that appeared in the meantime.
        inspection = await Task.Run(() => GameToolInstaller.Inspect(tool, target.TargetDirectory, null), token)
            .ConfigureAwait(false);
        var configuration = await PrepareWorkingConfigAsync(tool, appId, target, request.Settings).ConfigureAwait(false);
        tool = configuration.Tool;
        package = package with { SharedSettingsDirectory = configuration.Directory.Length > 0
            ? configuration.Directory : package.SharedSettingsDirectory };
        var outcome = await _installer.InstallAsync(tool, target, package,
                new GameToolRequest(request.ReplaceExisting ? "replaceExisting" : "install", proxy, configuration.Settings,
                    Channel: channel.Id),
                null, () => _running.ClosedMessage(target), phase => context.Phase(phase, false), token, inspection)
            .ConfigureAwait(false);
        GameToolStorage.RememberIdentity(_userRoot, appId, scan.IsShortcut, scan.Name);
        return outcome.Notice;
    }

    private async Task<string> ReinstallCoreAsync(GameToolDefinition tool, uint appId, string operation,
        Func<GameToolManifest, CancellationToken, Task<(HostedRelease Release, GameToolChannel Channel)>> release,
        bool replaceConfig, OperationContext context, CancellationToken token)
    {
        var (target, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
        tool = tool.ForGame(GameToolStorage.ReadLocked(_userRoot).Find(appId)?.IsShortcut == true);
        GameToolAdmission.RequireCompleted(manifest);
        if (!manifest.Enabled)
        {
            throw new GameToolException("Enable the tool before updating or repairing it.");
        }
        await RequireClosedAsync(target).ConfigureAwait(false);
        var (chosen, channel) = await release(manifest, token).ConfigureAwait(false);
        context.Release(channel, chosen);
        var package = await Releases.PrepareAsync(tool, channel, chosen, context.Progress, token,
                target.Architecture, manifest.ProxyName)
            .ConfigureAwait(false);
        package = await WithSharedPluginAsync(tool, tool.InstallMode(manifest.ProxyName), appId, package, context, token)
            .ConfigureAwait(false);

        // Installed add-ons go along with the latest build, so an update or repair keeps what the user chose.
        package = await WithAddonsAsync(tool, package,
            manifest.Addons.Select(record => tool.Addon(record.Id)).OfType<GameToolAddon>(), manifest,
            target.Architecture, context, token).ConfigureAwait(false);
        context.Phase(GameToolPhase.Copy, false);
        var configuration = await PrepareWorkingConfigAsync(tool, appId, target, new Dictionary<string, string>()).ConfigureAwait(false);
        tool = configuration.Tool;
        package = package with { SharedSettingsDirectory = configuration.Directory.Length > 0
            ? configuration.Directory : package.SharedSettingsDirectory };
        var outcome = await _installer.InstallAsync(tool, target, package,
                new GameToolRequest(operation, manifest.ProxyName,
                    configuration.Settings, replaceConfig, channel.Id),
                manifest, () => _running.ClosedMessage(target), phase => context.Phase(phase, false), token)
            .ConfigureAwait(false);
        return outcome.Notice;
    }

    // A fresh install takes the add-ons the user turned on, and otherwise those recommended for this game: on its
    // support list and useful on this machine's graphics.
    private async Task<GameToolPackage> WithSharedPluginAsync(GameToolDefinition tool, GameToolMode mode,
        uint appId, GameToolPackage package, OperationContext context, CancellationToken token,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        if (tool.SharedSettingsFile.Length > 0)
        {
            var folder = Path.Combine(SpecialKPluginLocation.Resolve(create: true), "Global");
            _sharedPlugins.Register(folder, appId);
            var requested = (settings ?? new Dictionary<string, string>()).Where(pair =>
                    tool.Settings.First(setting => setting.Id == pair.Key).Scope == "shared")
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            if (requested.Count > 0)
            {
                await _sharedPlugins.ConfigureAsync(folder, tool.SharedSettingsFile, tool, requested, package.Architecture,
                    () => SharedPluginClosed(folder)).ConfigureAwait(false);
            }

            package = package with { SharedSettingsDirectory = folder };
        }

        if (!mode.UsesSharedPlugin)
        {
            return package;
        }

        var dependency = tool.SharedDependency ?? throw new GameToolException("The required shared plugin is not described.");
        var directory = Path.Combine(SpecialKPluginLocation.Resolve(create: true), dependency.RelativeDirectory);
        var plugin = GameToolSharedPlugins.Definition(tool);
        var release = await LatestReleaseAsync(plugin, plugin.DefaultChannel, token).ConfigureAwait(false);
        var prepared = await Releases.PrepareAsync(plugin, plugin.DefaultChannel, release, context.Progress, token,
            package.Architecture, plugin.Proxy.Default).ConfigureAwait(false);
        await _sharedPlugins.EnsureAsync(appId, directory, plugin, prepared, () => SharedPluginClosed(directory), token)
            .ConfigureAwait(false);
        return package with { SharedDependencyDirectory = directory };
    }

    private string? SharedPluginClosed(string directory)
    {
        foreach (var game in GameToolStorage.ReadLocked(_userRoot).Games)
        {
            foreach (var installed in game.Tools.Where(tool => (tool.SharedDependencyDirectory.Length > 0
                         && StoragePaths.Same(tool.SharedDependencyDirectory, directory)) || (tool.SharedSettingsDirectory.Length > 0
                         && StoragePaths.Same(tool.SharedSettingsDirectory, directory))))
            {
                if (_running.ClosedMessage(GameToolTarget.FromManifest(game.AppId, installed.InstallRoot, installed)) is not null)
                {
                    return "Close " + game.Name + " before updating its shared Special K plugin.";
                }
            }
        }

        return null;
    }

    private async Task<(GameToolDefinition Tool, IReadOnlyDictionary<string, string> Settings, string Directory)>
        PrepareWorkingConfigAsync(GameToolDefinition tool, uint appId, GameToolTarget target, IReadOnlyDictionary<string, string> requested)
    {
        if (!tool.ConfigFromWorkingDirectory)
        {
            return (tool, requested, "");
        }

        string folder;
        lock (_sync)
        {
            folder = _scans.GetValueOrDefault(appId)?.WorkingDirectory ?? "";
        }

        if (folder.Length == 0)
        {
            if (requested.Count > 0)
            {
                throw new GameToolException(GameToolLaunchDirectory.Unavailable);
            }

            return (tool with { ManageConfiguration = false, Settings = [] }, requested, "");
        }

        if (StoragePaths.Same(folder, target.TargetDirectory))
        {
            return (tool, requested, "");
        }

        if (GameToolInstaller.ProbeWritable(target.TargetDirectory) is { } blocked)
        {
            throw new GameToolException(blocked);
        }

        _sharedPlugins.Register(folder, appId);
        var owner = tool with { Settings = [.. tool.Settings.Select(setting => setting with { Scope = "shared" })] };
        await _sharedPlugins.ConfigureAsync(folder, tool.PrimaryConfig, owner, requested, target.Architecture,
            () => SharedPluginClosed(folder)).ConfigureAwait(false);
        var native = GameToolInstaller.ReadSettings(tool, folder) ?? new Dictionary<string, string>();
        var canonical = new Dictionary<string, string>(requested, StringComparer.Ordinal);
        foreach (var setting in tool.Settings)
        {
            if (setting.UnavailableReason.Length == 0 && native.TryGetValue(setting.Id, out var raw)
                && setting.FromRaw(raw) is { } value)
            {
                canonical[setting.Id] = value;
            }
        }

        return (tool, canonical, folder);
    }

    private async Task<IReadOnlyList<GameToolAddon>> InstallAddonsAsync(GameToolDefinition tool,
        GameToolsInstallRequest request, string program, CancellationToken token)
    {
        List<GameToolAddon> chosen = [];
        foreach (var addon in tool.Addons)
        {
            var requested = request.Addons?.TryGetValue(addon.Id, out var on) == true ? on : (bool?)null;
            if (requested == false)
            {
                continue;
            }

            var support = await Releases.SupportAsync(tool, addon, false, token).ConfigureAwait(false);
            var supported = support.Supports(program);
            if (requested == true && !supported)
            {
                throw new GameToolException(support.Error ?? addon.Name + " doesn't support " + program + ".");
            }

            if (supported && (requested == true
                              || _vendors.Value.Any(vendor => addon.Vendors.Contains(vendor, StringComparer.Ordinal))))
            {
                chosen.Add(addon);
            }
        }

        return chosen;
    }

    // Downloads each add-on's latest build. When the lookup fails, the build an installation already has is used
    // again from the package cache, so a repair works offline.
    private async Task<GameToolPackage> WithAddonsAsync(GameToolDefinition tool, GameToolPackage package,
        IEnumerable<GameToolAddon> addons, GameToolManifest? manifest, string architecture, OperationContext context,
        CancellationToken token)
    {
        List<GameToolAddonPackage> prepared = [];
        foreach (var addon in addons)
        {
            var latest = await Releases.LatestAddonAsync(tool, addon, false, token).ConfigureAwait(false);
            var record = manifest?.Addons.FirstOrDefault(item => item.Id == addon.Id);
            var release = latest.Release ?? (record is null
                ? throw new GameToolException(latest.Error ?? "No build of " + addon.Name + " is available.")
                : new HostedRelease(record.ReleaseVersion, record.ReleaseId, "", addon.Source.Provider,
                    new ReleaseAsset(record.AssetName, record.SourceUrl, record.AssetSha256)));
            context.AddonDownload(addon, release);
            prepared.Add(await Releases.PrepareAddonAsync(tool, addon, release, architecture,
                (bytes, total) => context.AddonProgress(addon, release, bytes, total), token).ConfigureAwait(false));
        }

        return package with { Addons = prepared };
    }

    private async Task<HostedRelease> LatestReleaseAsync(GameToolDefinition tool, GameToolChannel channel,
        CancellationToken token)
    {
        var latest = await Releases.LatestAsync(tool, channel, false, token).ConfigureAwait(false);
        return latest.Release ?? throw new GameToolException(latest.Error ?? "No "
            + channel.Label.ToLowerInvariant() + " release of " + tool.Name + " is available.");
    }

    // Configure, remove and the reinstalls act on the manifest's own target, so the cached walk is reused.
    private async Task<(GameToolTarget Target, GameToolManifest Manifest)> InstalledAsync(GameToolDefinition tool,
        uint appId)
    {
        var scan = await ScanAsync(appId, CancellationToken.None, true).ConfigureAwait(false);
        var manifest = GameToolAdmission.RequireInstalled(tool, scan.HasFolder, scan.Reason, scan.Record);
        return (GameToolTarget.FromManifest(appId, scan.InstallFolder, manifest), manifest);
    }

    // The check lists folders and processes, so it runs off the caller's thread.
    private async Task RequireClosedAsync(GameToolTarget target)
    {
        if (await Task.Run(() => _running.ClosedMessage(target)).ConfigureAwait(false) is { } running)
        {
            throw new GameToolException(running);
        }
    }

    // Finds the tool and checks the request before admitting the work, then runs it behind the gate.
    // Subject names what the phase lines talk about when it isn't the tool itself, such as an add-on.
    private Task<SteamUiCommandResult> Start(uint appId, string toolId, string operation,
        Func<GameToolDefinition, string?>? validate,
        Func<GameToolDefinition, OperationContext, CancellationToken, Task<string>> body,
        Func<GameToolDefinition, string?>? subject = null)
    {
        if (Catalog.Find(toolId) is not { } tool)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Unknown tool."));
        }

        if (validate?.Invoke(tool) is { } invalid)
        {
            return Task.FromResult(new SteamUiCommandResult(false, invalid));
        }

        if (appId == 0)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "No game was supplied."));
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Game Tools is stopping."));
            }

            if (_operation is { IsCompleted: false })
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "Another Game Tools change is running. Wait for it to finish."));
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _operationCancellation = cancellation;
            var name = subject?.Invoke(tool) ?? tool.Name;
            _phaseStarted = Environment.TickCount64;
            _busy = new GameToolsBusy(appId, tool.Id, operation, GameToolPhase.Starting,
                GameToolsPhases.Detail(GameToolPhase.Starting, operation, name, null, null), 0, 0, true);
            _messages[appId] = (null, null);
            var context = new OperationContext(this, name, operation);
            _operation = Task.Run(() => RunAsync(appId, tool.Id, operation, context,
                token => body(tool, context, token), cancellation));
            _operation.ObserveFaults();
        }

        Changed?.Invoke();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private async Task RunAsync(uint appId, string toolId, string operation, OperationContext context,
        Func<CancellationToken, Task<string>> body, CancellationTokenSource cancellation)
    {
        string? error = null;
        string? notice = null;
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            entered = true;
            notice = await body(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            notice = GameToolInstaller.CancelledNotice;
            Log.Info($"Game Tools: {operation} {toolId} for {appId} cancelled.");
        }
        catch (GameToolException ex)
        {
            error = ex.Message;
            Log.Warn($"Game Tools: {operation} {toolId} for {appId} refused or failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or HttpRequestException or TimeoutException)
        {
            error = ex.Message;
            Log.Warn($"Game Tools: {operation} {toolId} for {appId} failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = "The change failed: " + ex.Message;
            Log.Error($"Game Tools: {operation} {toolId} for {appId} failed", ex);
        }

        try
        {
            if (entered && !_lifetime.IsCancellationRequested)
            {
                // The tool's files changed, not the game's programs: the walk is reused and only the record,
                // the inspection and the upscaler filter are read again.
                context.Phase(GameToolPhase.Rescan, false);
                _sharedPlugins.Reconcile(SharedPluginClosed);
                await ScanAsync(appId, CancellationToken.None, true).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Game Tools: rescan of {appId} after {operation} failed: {ex.Message}");
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }

            lock (_sync)
            {
                LogPhaseEnd(_busy);
                _busy = null;
                _messages[appId] = (error, notice);
                if (ReferenceEquals(_operationCancellation, cancellation))
                {
                    _operationCancellation = null;
                }
            }

            cancellation.Dispose();
            Changed?.Invoke();
        }
    }

    private void StartRefresh(uint appId)
    {
        lock (_sync)
        {
            if (_disposed || !_loading.Add(appId))
            {
                return;
            }
        }

        Changed?.Invoke();
        Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    await ScanAsync(appId, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // The session is stopping.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"Game Tools: scan of {appId} failed", ex);
                RecordScanFailure(appId, ex);
            }
            finally
            {
                lock (_sync)
                {
                    _loading.Remove(appId);
                }

                Changed?.Invoke();
            }

            StartLatestLookups(appId, false);
        }).ObserveFaults();
    }

    private void StartLatestLookups(uint appId, bool force)
    {
        foreach (var tool in Catalog.Tools)
        {
            foreach (var channel in tool.Channels)
            {
                StartLatestLookup(appId, tool, channel, force);
            }

            foreach (var addon in tool.Addons)
            {
                StartAddonLookup(tool, addon, force);
            }
        }
    }

    // An add-on's latest build and its support list are read together; the row appears once the list names the
    // game's program.
    private void StartAddonLookup(GameToolDefinition tool, GameToolAddon addon, bool force)
    {
        if (!force && !Releases.AddonLookupDue(tool, addon))
        {
            return;
        }

        var key = CheckingKey(tool.Id, "addon:" + addon.Id);
        lock (_sync)
        {
            if (_disposed || !_checking.Add(key))
            {
                return;
            }
        }

        Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(Releases.LatestAddonAsync(tool, addon, force, _lifetime.Token),
                    Releases.SupportAsync(tool, addon, force, _lifetime.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The session is stopping.
            }
            finally
            {
                lock (_sync)
                {
                    _checking.Remove(key);
                }

                RecomposeOpenGames();
                Changed?.Invoke();
            }
        }).ObserveFaults();
    }

    // Every channel has its own lookup and cache. A forced check reports the outcome of the channel the game's
    // tool follows; the other channels' answers only update their rows.
    private void StartLatestLookup(uint appId, GameToolDefinition tool, GameToolChannel channel, bool force)
    {
        if (!force && !Releases.LookupDue(tool, channel))
        {
            return;
        }

        var key = CheckingKey(tool.Id, channel.Id);
        lock (_sync)
        {
            if (_disposed || !_checking.Add(key))
            {
                return;
            }
        }

        Changed?.Invoke();
        Task.Run(async () =>
        {
            try
            {
                var latest = await Releases.LatestAsync(tool, channel, force, _lifetime.Token).ConfigureAwait(false);
                if (force && FollowedChannel(appId, tool) == channel)
                {
                    SetMessage(appId, latest.Error, latest.Error is null ? "Checked for updates." : null);
                }
            }
            catch (OperationCanceledException)
            {
                // The session is stopping.
            }
            finally
            {
                lock (_sync)
                {
                    _checking.Remove(key);
                }

                RecomposeOpenGames();
                QueueLibrary();
                Changed?.Invoke();
            }
        }).ObserveFaults();
    }

    private GameToolChannel FollowedChannel(uint appId, GameToolDefinition tool)
    {
        lock (_sync)
        {
            return tool.ChannelOrDefault(_scans.GetValueOrDefault(appId)?.Record?.Manifest(tool.Id)?.Channel);
        }
    }

    private static string CheckingKey(string toolId, string channelId)
    {
        return toolId + "/" + channelId;
    }

    // Peek's live lookup flags: per channel, and for the tool while any of its channels is looked up.
    private GameToolsTool WithChecking(GameToolsTool tool)
    {
        var channels = tool.Channels.Select(channel =>
            channel with { CheckingLatest = _checking.Contains(CheckingKey(tool.Id, channel.Id)) }).ToArray();
        return tool with { CheckingLatest = channels.Any(channel => channel.CheckingLatest), Channels = channels };
    }

    private void QueueLibrary()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                QueueLibraryLocked();
            }
        }
    }

    // One read runs at a time; a change while it runs reads once more afterwards.
    private void QueueLibraryLocked()
    {
        if (_libraryRead is { IsCompleted: false })
        {
            _libraryDirty = true;
            return;
        }

        _libraryRead = Task.Run(BuildLibrary, CancellationToken.None);
    }

    private GameToolsLibrary BuildLibrary()
    {
        GameToolsLibrary library;
        try
        {
            var store = GameToolStorage.ReadLocked(_userRoot);
            Dictionary<uint, string> roots;
            lock (_sync)
            {
                roots = _scans.Where(scan => scan.Value.InstallFolder.Length > 0)
                    .ToDictionary(scan => scan.Key, scan => scan.Value.InstallFolder);
            }

            library = GameToolsLibraryProjection.Build(store, Catalog,
                (tool, channel) => Releases.Cached(tool, channel)?.Release, appId => roots.GetValueOrDefault(appId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or JsonException)
        {
            Log.Warn($"Game Tools: reading the game list failed: {ex.Message}");
            library = new GameToolsLibrary(false, "WSGM couldn't read its Game Tools records: " + ex.Message, [], 0,
                0);
        }

        lock (_sync)
        {
            _library = library;
            _libraryRead = null;
            if (_libraryDirty && !_disposed)
            {
                _libraryDirty = false;
                _libraryRead = Task.Run(BuildLibrary, CancellationToken.None);
            }
        }

        Changed?.Invoke();
        return library;
    }

    // A release answer changes only the release-derived fields. Only the games a page shows are composed
    // again; any other cached game is scanned afresh when it is next opened.
    private void RecomposeOpenGames()
    {
        uint[] open;
        lock (_sync)
        {
            open = [.. new[] { _appId, _viewedAppId }.Where(id => id != 0).Distinct()];
            foreach (var cached in _scans.Keys.Where(id => !open.Contains(id)))
            {
                _releaseStale.Add(cached);
            }
        }

        foreach (var appId in open)
        {
            try
            {
                Recompose(appId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Game Tools: updating the state of {appId} failed: {ex.Message}");
            }
        }
    }

    // Composes outside the lock and swaps the result in only while its scan is still the current one; a scan
    // that finished meanwhile is composed again.
    private void Recompose(uint appId)
    {
        while (true)
        {
            GameToolsScan? scan;
            lock (_sync)
            {
                if (_disposed || !_scans.TryGetValue(appId, out scan))
                {
                    return;
                }
            }

            var state = Compose(scan);
            lock (_sync)
            {
                if (ReferenceEquals(_scans.GetValueOrDefault(appId), scan))
                {
                    _states[appId] = state;
                    return;
                }
            }
        }
    }

    private bool IsFresh(uint appId)
    {
        lock (_sync)
        {
            return _scans.TryGetValue(appId, out var scan) && !_releaseStale.Contains(appId)
                                                           && Environment.TickCount64 - scan.ScannedAt < FreshScan;
        }
    }

    // Callers hold the gate, so scans never overlap. With reuseLayout, a cached walk of the same root is
    // used instead of walking the folder again.
    private async Task<GameToolsScan> ScanAsync(uint appId, CancellationToken token, bool reuseLayout = false)
    {
        var shortcut = SteamApps.IsShortcutAppId(appId);
        var steam = _steam();
        var details = steam is null
            ? null
            : await SteamLaunchConfig.ReadAsync(steam, appId, token).ConfigureAwait(false);
        var record = GameToolStorage.ReadLocked(_userRoot).Find(appId);
        var name = details?.Name ?? record?.Name ?? appId.ToString(CultureInfo.InvariantCulture);
        GameToolsScan scan;
        if (details is null)
        {
            scan = GameToolsScan.Unavailable(appId, name, shortcut, "", "steamUnavailable",
                "Steam isn't reachable or did not report this game.", record);
        }
        else
        {
            var (root, directTarget, reason) = GameToolStatus.InstallRoot(shortcut, details);
            if (root.Length == 0)
            {
                scan = GameToolsScan.Unavailable(appId, name, shortcut, "", "notInstalled", reason, record);
            }
            else if (!Directory.Exists(root))
            {
                scan = GameToolsScan.Unavailable(appId, name, shortcut, root, "folderUnavailable",
                    "The game's folder isn't available. Connect the drive or library it is on.", record);
            }
            else
            {
                GameFolderLayout? cached = null;
                if (reuseLayout)
                {
                    lock (_sync)
                    {
                        cached = _scans.GetValueOrDefault(appId)?.Layout;
                    }
                }

                var layout = cached is not null && StoragePaths.Same(cached.Root, root) ? cached : null;
                scan = await Task.Run(() => ScanFolder(appId, name, shortcut, root, directTarget, record, layout,
                        GameToolLaunchDirectory.Resolve(appId, shortcut, details, root)),
                    token).ConfigureAwait(false);
            }
        }

        var state = await Task.Run(() => Compose(scan), CancellationToken.None).ConfigureAwait(false);
        lock (_sync)
        {
            _scans[appId] = scan;
            _states[appId] = state;
            _releaseStale.Remove(appId);
            if (scan.RecoveryNotice is { } recovered)
            {
                // An operation that scanned first replaces this with its own outcome when it finishes.
                _messages[appId] = (_messages.GetValueOrDefault(appId).Error, recovered);
            }

            // Recovery, a rebase or the finished operation may have changed the store the game list reads.
            if (!_disposed)
            {
                QueueLibraryLocked();
            }
        }

        return scan;
    }

    // Recovery runs first in every scan of an existing folder, so the page never shows a crash's half-finished
    // change as current and every mutation, which scans before it acts, starts from a reconciled record.
    private GameToolsScan ScanFolder(uint appId, string name, bool shortcut, string root, string? directTarget,
        GameToolRecord? record, GameFolderLayout? layout, string workingDirectory)
    {
        var recovery = _recovery.Recover(appId, root, record, Catalog);
        _sharedPlugins.Reconcile(SharedPluginClosed);
        record = recovery.Record;
        layout ??= GameFolderLayout.Scan(root);
        var resolution = GameRuntimeResolver.Resolve(layout, directTarget, record?.Runtime?.RelativePath, name,
            _architectures);
        IReadOnlySet<string> installed = record?.OwnedPaths(root) ?? new HashSet<string>();
        var upscalers = UpscalerScan.Scan(layout, installed);
        var antiCheat = AntiCheatScan.Scan(layout);
        var (availability, reason) = _running.FolderAvailability(appId, layout, resolution.Chosen?.FullPath);
        var evidence = resolution.Chosen is { } runtime ? GameGraphicsApi.Read(runtime.FullPath) : new GameGraphicsEvidence([], []);
        var libraries = resolution.Chosen is { } program
            ? GameToolBinaryIdentification.Scan(Path.GetDirectoryName(program.FullPath)!, program.FullPath) : [];
        return new GameToolsScan(appId, name, shortcut, root, availability, reason, resolution, layout.Truncated,
            upscalers, antiCheat, record, layout, Environment.TickCount64, recovery.Notice)
        {
            GraphicsApis = evidence.Pipelines, GraphicsImports = evidence.Imports, Libraries = libraries, WorkingDirectory = workingDirectory
        };
    }

    // Builds the published state from a scan. It reads the target folder and configuration file, so callers
    // never hold _sync while it runs.
    private GameToolsState Compose(GameToolsScan scan)
    {
        var resolution = scan.Resolution;
        var chosen = resolution?.Chosen;
        var runtime = chosen is null
            ? null
            : new GameToolsRuntime(chosen.RelativePath, chosen.FileName, chosen.Architecture,
                PeImage.Label(chosen.Architecture), GameToolStatus.ConfidenceId(resolution!.Confidence),
                GameToolStatus.ConfidenceLabel(resolution.Confidence))
            {
                GraphicsApi = scan.GraphicsApis is { Length: > 0 } apis
                    ? string.Join(", ", apis)
                    : "Unknown graphics API"
            };
        var candidates = resolution?.Candidates.Select(candidate => new GameToolsCandidate(candidate.RelativePath,
            candidate.FileName, candidate.Architecture, PeImage.Label(candidate.Architecture),
            GameToolStatus.RoleId(candidate.Role), GameToolStatus.RoleLabel(candidate.Role), candidate.Reasons,
            ReferenceEquals(candidate, chosen))).ToArray() ?? [];
        var tools = Catalog.Tools.Select(tool => ToolState(tool, scan)).ToArray();
        return new GameToolsState(scan.AppId, scan.Name, scan.IsShortcut, scan.InstallFolder, scan.Availability,
            scan.Reason, false, resolution?.EngineLabel ?? "Unknown engine", runtime, candidates,
            resolution?.RememberedMissing == true, scan.Truncated,
            [.. scan.Upscalers.Select(file => new GameToolsUpscaler(file.Technology, file.RelativePath, file.Version))],
            scan.AntiCheat, GameToolStatus.AntiCheatWarning(Catalog.Tools, scan.AntiCheat), _vendors.Value, tools,
            null, null, null) { Libraries = scan.Libraries };
    }

    private GameToolsTool ToolState(GameToolDefinition tool, GameToolsScan scan)
    {
        tool = tool.ForGame(scan.IsShortcut);
        var manifest = scan.Record?.Manifest(tool.Id);
        var followed = tool.ChannelOrDefault(manifest?.Channel);
        var latest = Releases.Cached(tool, followed);
        var chosen = scan.Resolution?.Chosen;
        var targetDirectory = manifest is not null && scan.HasFolder
            ? GameToolPaths.Resolve(scan.InstallFolder, manifest.TargetDirectory)
            : chosen is not null
                ? Path.GetDirectoryName(chosen.FullPath)
                : null;
        var inspection = scan.HasFolder && targetDirectory is not null && Directory.Exists(targetDirectory)
            ? GameToolInstaller.Inspect(tool, targetDirectory, manifest, scan.Libraries)
            : null;
        var evaluation = GameToolStatus.Evaluate(tool, scan.Availability, scan.Reason, chosen?.Architecture, manifest,
            inspection, latest?.Release, followed, scan.Upscalers);
        var channels = tool.Channels.Select(channel =>
        {
            var answer = channel == followed ? latest : Releases.Cached(tool, channel);
            var release = answer?.Release;
            return new GameToolsChannel(channel.Id, channel.Label, channel.Description, channel == followed,
                release?.Version, release is null ? null : channel.Display(release.Version, release.Asset.Name),
                release is null
                    ? null
                    : channel.BuildDate(release.Asset.Name)?.ToString("yyyy-MM-dd",
                        CultureInfo.InvariantCulture), answer?.Error, false,
                GameToolStatus.ChangesConfig(tool, manifest, release));
        }).ToArray();
        var options = tool.ProxyOptions;
        var proxies = options.Select(option =>
        {
            var status = inspection?.Proxies.FirstOrDefault(proxy => proxy.Name == option.Name);
            var mode = tool.InstallMode(option.Name);
            var reason = chosen is not null && !mode.Architectures.Contains(chosen.Architecture)
                ? "This file set is not shipped for " + PeImage.Label(chosen.Architecture) + " games."
                : mode.RequiredImports.Any(name => !scan.GraphicsImports.Contains(name, StringComparer.OrdinalIgnoreCase))
                    ? "The game does not import " + string.Join(", ", mode.RequiredImports) + "." : status?.Conflict;
            return new GameToolsProxyOption(option.Name, option.Hint,
                reason is null && (status?.Available ?? true) && (chosen is null || mode.Architectures.Contains(chosen.Architecture)),
                reason)
            {
                Api = option.Api,
                Files = [.. mode.Files.Values.Select(file => file == tool.PrimaryFile ? option.Name : file)
                    .Concat(tool.ManageConfiguration && (!tool.ConfigFromWorkingDirectory || scan.WorkingDirectory.Length > 0)
                        ? [tool.ForProxy(option.Name).PrimaryConfig] : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)],
                Dependency = mode.UsesSharedPlugin ? "Uses the managed dgVoodoo plugin" : ""
            };
        }).ToArray();
        var inspected = inspection?.Proxies ?? [.. options.Select(option =>
            new GameToolProxyStatus(option.Name, option.Hint, true, null, option.Suggest))];
        var proxyName = manifest?.ProxyName ?? GameToolInstaller.SuggestProxy(inspected, tool.Proxy.Default,
            tool.UsesGraphicsApi ? tool : null, scan.GraphicsApis, chosen?.Architecture, scan.GraphicsImports);
        if (manifest is null && inspection is { Unmanaged.Count: > 0 })
        {
            var existing = tool.InstallModes.Where(mode => inspection.Unmanaged.Any(copy =>
                copy.RelativePath.Equals(mode.Proxy, StringComparison.OrdinalIgnoreCase))).ToArray();
            var matching = existing.Where(mode => mode.Apis.Intersect(scan.GraphicsApis).Any()).ToArray();
            var candidates = matching.Length > 0 ? matching : existing;
            proxyName = scan.GraphicsApis.Length <= 1 && candidates.Length > 0
                ? candidates.OrderByDescending(mode => mode.Priority).First().Proxy : "";
            proxies = [.. proxies.Select(option => inspection.Unmanaged.Any(copy => copy.RelativePath.Equals(option.Name,
                    StringComparison.OrdinalIgnoreCase)) ? option with { Available = chosen is null || tool.InstallMode(option.Name)
                    .Architectures.Contains(chosen.Architecture), Conflict = null } : option)];
        }
        var iniValues = targetDirectory is not null
            ? GameToolInstaller.ReadSettings(tool.ForProxy(proxyName.Length > 0 ? proxyName : tool.Proxy.Default), targetDirectory) : null;
        string? sharedReason = null;
        if (tool.SharedSettingsFile.Length > 0 && scan.HasFolder && chosen is not null)
        {
            try
            {
                var folder = manifest?.SharedSettingsDirectory is { Length: > 0 } saved ? saved
                    : Path.Combine(SpecialKPluginLocation.Resolve(create: false), "Global");
                var sharedTool = tool with { ConfigFiles = [tool.SharedSettingsFile],
                    Settings = [.. tool.Settings.Where(setting => setting.Scope == "shared")] };
                var global = GameToolInstaller.ReadSettings(sharedTool, folder);
                iniValues ??= new Dictionary<string, string>();
                foreach (var pair in global ?? new Dictionary<string, string>())
                {
                    iniValues[pair.Key] = pair.Value;
                }
            }
            catch (GameToolException ex)
            {
                sharedReason = ex.Message;
            }
        }

        var workingUnavailable = tool.ConfigFromWorkingDirectory && scan.WorkingDirectory.Length == 0;
        if (tool.ConfigFromWorkingDirectory && !workingUnavailable)
        {
            iniValues = GameToolInstaller.ReadSettings(tool, scan.WorkingDirectory);
        }

        var settings = tool.Settings.Select(setting =>
        {
            var value = GameToolStatus.SettingValue(setting, manifest, iniValues, _vendors.Value);
            return new GameToolsSetting(setting.Id, setting.Label, setting.Description, value.Value, value.IniValue,
                [.. setting.Options.Select(option => new GameToolsSettingOption(option.Value, option.Label))],
                value.Available && !workingUnavailable && setting.UnavailableReason.Length == 0
                    && !(setting.Scope == "shared" && sharedReason is not null),
                workingUnavailable ? GameToolLaunchDirectory.Unavailable : setting.UnavailableReason.Length > 0
                    ? setting.UnavailableReason : sharedReason ?? value.Note)
            {
                Kind = setting.Kind, Scope = setting.Scope, Group = setting.Scope == "shared" ? "All Special K games" : setting.Group,
                HotkeyFormat = setting.HotkeyFormat,
                KeyChoices = setting.Kind == "hotkey" ? [.. GameToolHotkeys.Keys.Select(key => new GameToolsSettingOption(
                    key.Key.ToString(CultureInfo.InvariantCulture), key.Label))] : []
            };
        }).ToArray();
        var relativeTarget = targetDirectory is null
            ? ""
            : GameToolPaths.RelativeDirectory(scan.InstallFolder, targetDirectory);

        // An add-on checks the running program's name: the one the tool was installed beside, else the chosen one.
        var program = manifest is not null ? Path.GetFileName(manifest.RuntimeExecutable) : chosen?.FileName;
        var addons = tool.Addons.Select(addon => (Addon: addon, State: GameToolStatus.Addon(addon, manifest, program,
                Releases.CachedSupport(tool, addon), Releases.CachedAddon(tool, addon)?.Release, _vendors.Value,
                evaluation.ShowOptions, evaluation.CanEditOptions)))
            .Where(entry => entry.State.Show)
            .Select(entry => new GameToolsAddon(entry.Addon.Id, entry.Addon.Name, entry.Addon.Description,
                entry.Addon.Homepage, entry.Addon.License, entry.State.Support, entry.State.Installed,
                entry.State.Recommended, entry.State.CanChange, entry.State.Text, entry.State.Note,
                entry.State.UpdateAvailable)).ToArray();
        return new GameToolsTool(tool.Id, tool.Name, tool.Description, tool.Homepage, tool.License, tool.Notice,
            evaluation.IncompatibleReason, evaluation.Status, evaluation.StatusText,
            manifest is null ? null : GameToolVersions.Installed(manifest),
            manifest is null ? null : followed.Display(GameToolVersions.Installed(manifest), manifest.AssetName),
            followed.Id, channels, latest?.Release?.Version, channels.First(channel => channel.Current).LatestLabel,
            latest?.Error, false, evaluation.UpdateChangesConfig, proxyName,
            manifest is null ? tool.Proxy.Intro : tool.Proxy.RenameIntro, proxies, settings, evaluation.ShowOptions,
            evaluation.CanEditOptions,
            [
                .. evaluation.Conflicts.Select(conflict => new GameToolsConflict(conflict.RelativePath, conflict.Kind,
                    GameToolStatus.KindLabel(conflict.Kind), conflict.Message))
            ],
            evaluation.Warnings, evaluation.Actions,
            new GameToolsFiles(relativeTarget, manifest?.Files.Count ?? 0, manifest?.Replaced.Count ?? 0,
                inspection?.Missing ?? [], inspection?.Modified ?? []), addons)
        {
            Enabled = manifest?.Enabled ?? true,
            ProxyLabel = tool.UsesGraphicsApi ? "Graphics API" : "Load as",
            ProxyEditable = evaluation.CanEditOptions && (manifest?.Enabled ?? true) && !(tool.UsesGraphicsApi && manifest is not null),
            SettingsEditable = evaluation.CanEditOptions && !workingUnavailable,
            SettingsNotice = workingUnavailable ? GameToolLaunchDirectory.Unavailable : tool.ManageConfiguration
                ? "Settings apply on the next game start. Existing keys are preserved unless you change them."
                : "",
            ProxyIntro = tool.UsesGraphicsApi && manifest is not null
                ? "Remove and reinstall to change the graphics API."
                : manifest is not null ? "Choosing another name renames the installed file."
                : tool.UsesGraphicsApi && proxyName.Length == 0
                    ? scan.GraphicsApis.Length > 1
                        ? "Multiple render pipelines were detected. Choose the one this game uses."
                        : "No compatible free file set was selected. Choose the renderer this game uses; unsupported architectures and occupied DLLs are refused."
                    : ""
        };
    }

    private static IReadOnlyList<string> DetectVendors(Func<IReadOnlyList<GpuVendor>> detect)
    {
        try
        {
            return
                [.. detect().Select(vendor => vendor.ToString().ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Game Tools: GPU vendor detection failed: {ex.Message}");
            return [];
        }
    }

    private static GameToolsState Placeholder(uint appId)
    {
        return new GameToolsState(appId, appId.ToString(CultureInfo.InvariantCulture), SteamApps.IsShortcutAppId(appId),
            "", "ready", null, true, "Unknown engine", null, [], false, false, [], [], null, [], [], null, null, null);
    }

    private bool IsBusy()
    {
        lock (_sync)
        {
            return _operation is { IsCompleted: false };
        }
    }

    private SteamUiCommandResult Fail(uint appId, string message)
    {
        SetMessage(appId, message, null);
        return new SteamUiCommandResult(false, message);
    }

    // A failed first scan publishes an unavailable state rather than the loading placeholder, so both
    // pages show the failure with Refresh enabled; a game scanned before keeps its last state.
    private void RecordScanFailure(uint appId, Exception ex)
    {
        lock (_sync)
        {
            _messages[appId] = ("WSGM couldn't read the game folder: " + ex.Message, null);
            if (_scans.ContainsKey(appId))
            {
                return;
            }

            _scans[appId] = GameToolsScan.Unavailable(appId, appId.ToString(CultureInfo.InvariantCulture),
                SteamApps.IsShortcutAppId(appId), "", "folderUnavailable",
                "WSGM couldn't read this game's folder. Refresh to try again.", null);
        }

        Recompose(appId);
    }

    private void SetMessage(uint appId, string? error, string? notice)
    {
        lock (_sync)
        {
            _messages[appId] = (error, notice);
        }
    }

    // One line per phase, never per progress report: a tester log then shows which step took the time.
    // Called under _sync.
    private void LogPhaseEnd(GameToolsBusy? busy)
    {
        if (busy is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        Log.Info($"Game Tools: {busy.Operation} {busy.ToolId} for {busy.AppId}: {busy.Phase} took {now - _phaseStarted} ms.");
        _phaseStarted = now;
    }

    private void Report(string phase, string detail, long bytes, long total, bool cancellable,
        (string Channel, string Version)? release = null)
    {
        bool raise;
        lock (_sync)
        {
            if (_busy is null)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (_busy.Phase != phase)
            {
                LogPhaseEnd(_busy);
            }

            raise = _busy.Phase != phase || _busy.Cancellable != cancellable || release is not null
                    || now - _lastProgress >= ProgressInterval;
            _busy = _busy with
            {
                Phase = phase, Detail = detail, Bytes = bytes, Total = total, Cancellable = cancellable,
                Channel = release?.Channel ?? _busy.Channel, Version = release?.Version ?? _busy.Version
            };
            if (raise)
            {
                _lastProgress = now;
            }
        }

        if (raise)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The session's process answers for the Core running check.</summary>
    /// <param name="appRunning">Whether Steam reports an AppID running; null when unknown.</param>
    private sealed class SessionProcessProbe(Func<uint, bool?> appRunning) : IGameProcessProbe
    {
        /// <inheritdoc />
        public bool? IsAppRunning(uint appId)
        {
            return appRunning(appId);
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, bool?> ReadRunningPaths(IReadOnlyList<string> executables)
        {
            return ProcessLookup.ReadRunningPaths(executables);
        }
    }

    /// <summary>Progress reporting handed to one operation.</summary>
    /// <param name="owner">The service publishing the progress.</param>
    /// <param name="subject">What the operation changes, as the phase lines name it: the tool or an add-on.</param>
    /// <param name="operation">The operation id.</param>
    private sealed class OperationContext(GameToolsService owner, string subject, string operation)
    {
        /// <summary>Names the add-on build being downloaded, still cancellable.</summary>
        /// <param name="addon">The add-on.</param>
        /// <param name="release">Its release.</param>
        internal void AddonDownload(GameToolAddon addon, HostedRelease release)
        {
            AddonProgress(addon, release, 0, release.Asset.Size);
        }

        /// <summary>Reports an add-on download's bytes.</summary>
        /// <param name="addon">The add-on.</param>
        /// <param name="release">Its release.</param>
        /// <param name="bytes">Bytes copied.</param>
        /// <param name="total">The expected total.</param>
        internal void AddonProgress(GameToolAddon addon, HostedRelease release, long bytes, long total)
        {
            owner.Report(GameToolPhase.Download, GameToolsPhases.Detail(GameToolPhase.Download, operation,
                addon.Name, null, release.Provider.Length > 0 ? release.Provider : addon.Source.Provider), bytes,
                total, true);
        }

        private string? _provider;
        private string? _version;

        /// <summary>Records the release being installed, so the phase lines and the busy state can name it.</summary>
        /// <param name="channel">The channel it comes from.</param>
        /// <param name="release">The release.</param>
        internal void Release(GameToolChannel channel, HostedRelease release)
        {
            _version = channel.Display(release.Version, release.Asset.Name);
            _provider = release.Provider.Length > 0 ? release.Provider : channel.Source.Provider;
            owner.Report(GameToolPhase.Download, Detail(GameToolPhase.Download), 0, release.Asset.Size, true,
                (channel.Id, _version));
        }

        /// <summary>Reports download and extraction progress; still cancellable.</summary>
        /// <param name="phase">The <see cref="GameToolPhase" /> id.</param>
        /// <param name="bytes">Bytes copied.</param>
        /// <param name="total">The expected total.</param>
        internal void Progress(string phase, long bytes, long total)
        {
            owner.Report(phase, Detail(phase), bytes, total, true);
        }

        /// <summary>Reports a phase without byte progress.</summary>
        /// <param name="phase">The <see cref="GameToolPhase" /> id.</param>
        /// <param name="cancellable">Whether cancel can still stop the work.</param>
        internal void Phase(string phase, bool cancellable)
        {
            owner.Report(phase, Detail(phase), 0, 0, cancellable);
        }

        private string Detail(string phase)
        {
            return GameToolsPhases.Detail(phase, operation, subject, _version, _provider);
        }
    }
}
