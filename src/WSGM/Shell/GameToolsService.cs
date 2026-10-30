using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Core.Downloads;
using WSGM.Core.GameTools;

namespace WSGM.Shell;

/// <summary>Everything the Game Tools page shows for one game, shared by the Overlay and Steam.</summary>
/// <param name="AppId">The Steam AppID or non-Steam shortcut id.</param>
/// <param name="Name">The game's name.</param>
/// <param name="IsShortcut">Whether the id is a non-Steam shortcut.</param>
/// <param name="InstallFolder">The game's current install folder, empty when Steam reports none.</param>
/// <param name="Availability"><c>ready</c>, <c>notInstalled</c>, <c>folderUnavailable</c>, <c>steamUnavailable</c> or <c>running</c>.</param>
/// <param name="Reason">A user-facing explanation when the game is not <c>ready</c>.</param>
/// <param name="Loading">Whether a scan is running and the other fields may be placeholders.</param>
/// <param name="EngineLabel">The detected engine shown to the user.</param>
/// <param name="Runtime">The chosen runtime executable, or null when the user must choose.</param>
/// <param name="Candidates">Every executable the game might run, best first.</param>
/// <param name="RememberedMissing">Whether the program the user confirmed earlier is gone.</param>
/// <param name="ScanTruncated">Whether the folder was too large to scan completely.</param>
/// <param name="Upscalers">Standalone upscaler and frame-generation libraries the game ships.</param>
/// <param name="AntiCheat">Names of anti-cheat systems recognised in the folder.</param>
/// <param name="AntiCheatWarning">
///     The finished anti-cheat warning naming what was found, or null when none was; installing then needs
///     <c>acknowledgeRisk: true</c>.
/// </param>
/// <param name="GpuVendors">Lowercase GPU vendors present: <c>amd</c>, <c>intel</c>, <c>nvidia</c>.</param>
/// <param name="Tools">Every catalog tool and its state for this game.</param>
/// <param name="Busy">The running operation, or null.</param>
/// <param name="Error">The last failure for this game, or null.</param>
/// <param name="Notice">The last success or information message for this game, or null.</param>
internal sealed record GameToolsState(
    uint AppId,
    string Name,
    bool IsShortcut,
    string InstallFolder,
    string Availability,
    string? Reason,
    bool Loading,
    string EngineLabel,
    GameToolsRuntime? Runtime,
    IReadOnlyList<GameToolsCandidate> Candidates,
    bool RememberedMissing,
    bool ScanTruncated,
    IReadOnlyList<GameToolsUpscaler> Upscalers,
    IReadOnlyList<string> AntiCheat,
    string? AntiCheatWarning,
    IReadOnlyList<string> GpuVendors,
    IReadOnlyList<GameToolsTool> Tools,
    GameToolsBusy? Busy,
    string? Error,
    string? Notice);

/// <summary>The chosen runtime executable.</summary>
/// <param name="RelativePath">Forward-slash path relative to the install folder, or absolute outside it.</param>
/// <param name="FileName">The file name.</param>
/// <param name="Architecture"><c>x64</c>, <c>x86</c>, <c>arm64</c> or empty.</param>
/// <param name="ArchitectureLabel">The architecture shown to the user, empty when unknown.</param>
/// <param name="Confidence"><c>confirmed</c>, <c>structural</c>, <c>single</c> or <c>needsChoice</c>.</param>
/// <param name="ConfidenceLabel">How the program was chosen, empty when the user must choose.</param>
internal sealed record GameToolsRuntime(
    string RelativePath,
    string FileName,
    string Architecture,
    string ArchitectureLabel,
    string Confidence,
    string ConfidenceLabel);

/// <summary>One executable the user may choose.</summary>
/// <param name="RelativePath">Forward-slash path relative to the install folder, or absolute outside it.</param>
/// <param name="FileName">The file name.</param>
/// <param name="Architecture"><c>x64</c>, <c>x86</c>, <c>arm64</c> or empty.</param>
/// <param name="ArchitectureLabel">The architecture shown to the user, empty when unknown.</param>
/// <param name="Role"><c>game</c>, <c>launcher</c>, <c>helper</c> or <c>unknown</c>.</param>
/// <param name="RoleLabel">What the program most likely is, shown to the user.</param>
/// <param name="Reasons">Evidence, strongest first.</param>
/// <param name="Chosen">Whether it is the current runtime.</param>
internal sealed record GameToolsCandidate(
    string RelativePath,
    string FileName,
    string Architecture,
    string ArchitectureLabel,
    string Role,
    string RoleLabel,
    IReadOnlyList<string> Reasons,
    bool Chosen);

/// <summary>A standalone upscaler library the game ships.</summary>
/// <param name="Technology">The technology shown to the user.</param>
/// <param name="RelativePath">Forward-slash path relative to the install folder.</param>
/// <param name="Version">The file version, or null.</param>
internal sealed record GameToolsUpscaler(string Technology, string RelativePath, string? Version);

/// <summary>One tool's state for one game.</summary>
/// <param name="Id">The tool id.</param>
/// <param name="Name">The product name.</param>
/// <param name="Description">What it does.</param>
/// <param name="Homepage">The upstream project page.</param>
/// <param name="License">The upstream license identifier.</param>
/// <param name="Notice">The affiliation notice.</param>
/// <param name="IncompatibleReason">Why it can't be installed for the chosen runtime, or null when it can.</param>
/// <param name="Status"><c>notInstalled</c>, <c>installed</c>, <c>updateAvailable</c>, <c>modified</c>, <c>conflict</c> or <c>unavailable</c>.</param>
/// <param name="StatusText">A short user-facing status line.</param>
/// <param name="InstalledVersion">The installed product version, or null.</param>
/// <param name="LatestVersion">The latest stable release tag, or null when unknown.</param>
/// <param name="LatestError">Why the latest release is unknown, or null.</param>
/// <param name="CheckingLatest">Whether a release lookup is running.</param>
/// <param name="UpdateChangesConfig">Whether the available update changes the configuration format.</param>
/// <param name="ProxyName">The installed proxy name, or the suggested one when not installed.</param>
/// <param name="ProxyIntro">The text introducing the file name choice: before installation, or for a rename.</param>
/// <param name="ProxyOptions">Every offered proxy name.</param>
/// <param name="Settings">The tool's settings.</param>
/// <param name="ShowOptions">Whether the file name and settings are shown.</param>
/// <param name="CanEditOptions">Whether the file name and settings can be changed now.</param>
/// <param name="Conflicts">Files that block installation or need review.</param>
/// <param name="Warnings">Things the user should know before acting; the anti-cheat warning is game-level.</param>
/// <param name="Actions">Commands currently valid for this tool.</param>
/// <param name="Files">The ownership summary.</param>
internal sealed record GameToolsTool(
    string Id,
    string Name,
    string Description,
    string Homepage,
    string License,
    string Notice,
    string? IncompatibleReason,
    string Status,
    string StatusText,
    string? InstalledVersion,
    string? LatestVersion,
    string? LatestError,
    bool CheckingLatest,
    bool UpdateChangesConfig,
    string ProxyName,
    string ProxyIntro,
    IReadOnlyList<GameToolsProxyOption> ProxyOptions,
    IReadOnlyList<GameToolsSetting> Settings,
    bool ShowOptions,
    bool CanEditOptions,
    IReadOnlyList<GameToolsConflict> Conflicts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Actions,
    GameToolsFiles Files);

/// <summary>One proxy file name.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Hint">When to choose it.</param>
/// <param name="Available">Whether it can be used now.</param>
/// <param name="Conflict">Why not, or null.</param>
internal sealed record GameToolsProxyOption(string Name, string Hint, bool Available, string? Conflict);

/// <summary>One data-driven setting.</summary>
/// <param name="Id">The setting id.</param>
/// <param name="Label">The label.</param>
/// <param name="Description">A short explanation.</param>
/// <param name="Value">The selected option value; empty when the file holds a value no option matches.</param>
/// <param name="IniValue">The raw value in the configuration file, or null when not installed or missing.</param>
/// <param name="Options">The selectable options.</param>
/// <param name="Available">False when the installed version's configuration file has no such key.</param>
/// <param name="Note">A vendor-specific or availability note, or null.</param>
internal sealed record GameToolsSetting(
    string Id,
    string Label,
    string Description,
    string Value,
    string? IniValue,
    IReadOnlyList<GameToolsSettingOption> Options,
    bool Available,
    string? Note);

/// <summary>One option of a setting.</summary>
/// <param name="Value">The value sent back in commands.</param>
/// <param name="Label">The label.</param>
internal sealed record GameToolsSettingOption(string Value, string Label);

/// <summary>A file that blocks installation or needs review.</summary>
/// <param name="Path">The file relative to the target folder.</param>
/// <param name="Kind"><c>unmanagedTool</c> or <c>needsReview</c>.</param>
/// <param name="KindLabel">The kind shown to the user.</param>
/// <param name="Message">The explanation.</param>
internal sealed record GameToolsConflict(string Path, string Kind, string KindLabel, string Message);

/// <summary>What WSGM owns in the game folder.</summary>
/// <param name="TargetDirectory">The folder the tool is or would be installed into, relative to the install folder.</param>
/// <param name="Owned">Files WSGM created.</param>
/// <param name="Replaced">Game files WSGM backed up and replaced.</param>
/// <param name="Missing">Owned files that are gone.</param>
/// <param name="Modified">Owned files changed since installation.</param>
internal sealed record GameToolsFiles(
    string TargetDirectory,
    int Owned,
    int Replaced,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Modified);

/// <summary>The running operation.</summary>
/// <param name="AppId">The game it runs for.</param>
/// <param name="Operation"><c>install</c>, <c>replaceExisting</c>, <c>update</c>, <c>repair</c>, <c>configure</c> or <c>remove</c>.</param>
/// <param name="Phase">The current phase text.</param>
/// <param name="Bytes">Bytes downloaded, zero outside a download.</param>
/// <param name="Total">The expected download size, zero when unknown.</param>
/// <param name="Cancellable">Whether <c>cancel</c> can still stop it.</param>
internal sealed record GameToolsBusy(uint AppId, string Operation, string Phase, long Bytes, long Total,
    bool Cancellable);

/// <summary>An install request.</summary>
/// <param name="AppId">The game.</param>
/// <param name="ToolId">The tool.</param>
/// <param name="ProxyName">The proxy name, or null for the suggested one.</param>
/// <param name="Settings">Setting values by id; omitted ids use their defaults.</param>
/// <param name="AcknowledgeRisk">Whether the user accepted the anti-cheat warning.</param>
/// <param name="ReplaceExisting">Back up and replace an installation made outside WSGM.</param>
internal sealed record GameToolsInstallRequest(
    uint AppId,
    string ToolId,
    string? ProxyName,
    IReadOnlyDictionary<string, string> Settings,
    bool AcknowledgeRisk,
    bool ReplaceExisting);

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

    private readonly string _userRoot;
    private readonly Func<SteamClient?> _steam;
    private readonly GameRunningCheck _running;
    private readonly Lazy<GameToolReleases> _releases;
    private readonly GameToolInstaller _installer;
    private readonly GameToolRecovery _recovery;
    private readonly Lazy<IReadOnlyList<string>> _vendors;
    private readonly PeArchitectureCache _architectures = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Dictionary<uint, GameToolsScan> _scans = [];
    private readonly Dictionary<uint, GameToolsState> _states = [];
    private readonly Dictionary<uint, (string? Error, string? Notice)> _messages = [];
    private readonly HashSet<uint> _loading = [];
    private readonly HashSet<uint> _releaseStale = [];
    private readonly HashSet<string> _checking = new(StringComparer.Ordinal);
    private uint _appId;
    private uint _viewedAppId;
    private GameToolsBusy? _busy;
    private Task? _operation;
    private CancellationTokenSource? _operationCancellation;
    private long _lastProgress;
    private bool _disposed;

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
        _recovery = new GameToolRecovery(_userRoot, _installer, _running);
        var detect = vendors ?? GpuDriver.DetectVendors;
        _vendors = new Lazy<IReadOnlyList<string>>(() => DetectVendors(detect));
    }

    /// <summary>Raised after state, progress or a result changed, on whatever thread did the work.</summary>
    public event Action? Changed;

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

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RefreshAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartRefresh(appId);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>The Steam publication: the game opened from the cog menu.</summary>
    /// <returns>The state, or null when no game was opened.</returns>
    internal ValueTask<GameToolsState?> ReadStateAsync()
    {
        var appId = _appId;
        return new ValueTask<GameToolsState?>(appId == 0 ? null : Peek(appId));
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
                    Busy = busy, Loading = loading, Error = messages.Error, Notice = messages.Notice,
                    Tools = _checking.Count == 0
                        ? state.Tools
                        : [.. state.Tools.Select(tool => tool with { CheckingLatest = _checking.Contains(tool.Id) })]
                };
            }

            return Placeholder(appId) with { Busy = busy, Loading = true, Error = messages.Error, Notice = messages.Notice };
        }
    }

    /// <inheritdoc />
    public async Task<GameToolsState> ReadAsync(uint appId, CancellationToken cancellationToken = default)
    {
        if (appId == 0)
        {
            return Placeholder(0) with { Loading = false, Availability = "notInstalled", Reason = "No game was supplied." };
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
            tool => GameToolAdmission.Settings(tool, request.Settings),
            (tool, context, token) => InstallCoreAsync(tool, request, context, token));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> UpdateAsync(uint appId, string toolId, bool replaceConfig,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(appId, toolId, "update", null, (tool, context, token) => ReinstallCoreAsync(tool, appId, "update",
            (_, releaseToken) => LatestReleaseAsync(tool, releaseToken), "Updating " + tool.Name, replaceConfig, context,
            token));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RepairAsync(uint appId, string toolId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(appId, toolId, "repair", null, (tool, context, token) => ReinstallCoreAsync(tool, appId, "repair",
            (manifest, _) => Task.FromResult(GameToolReleases.FromManifest(manifest)), "Repairing " + tool.Name, false,
            context, token));
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
                context.Phase("Saving settings", false);
                var outcome = await Task.Run(() => _installer.Configure(tool, target, manifest, proxyName, settings,
                    () => _running.ClosedMessage(target)), CancellationToken.None).ConfigureAwait(false);
                return outcome.Notice;
            });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RemoveAsync(uint appId, string toolId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Start(appId, toolId, "remove", null, async (tool, context, _) =>
        {
            var (target, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
            await RequireClosedAsync(target).ConfigureAwait(false);
            context.Phase("Removing " + tool.Name, false);
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

    private async Task<string> InstallCoreAsync(GameToolDefinition tool, GameToolsInstallRequest request,
        OperationContext context, CancellationToken token)
    {
        var appId = request.AppId;
        var scan = await ScanAsync(appId, token).ConfigureAwait(false);
        var target = GameToolAdmission.RequireInstallable(tool, appId, scan.InstallFolder, scan.Availability,
            scan.Reason, scan.Record, scan.Resolution, scan.AntiCheat, request.AcknowledgeRisk);
        await RequireClosedAsync(target).ConfigureAwait(false);
        var release = await LatestReleaseAsync(tool, token).ConfigureAwait(false);
        var package = await Releases.PrepareAsync(tool, release, context.Progress, token).ConfigureAwait(false);
        context.Phase("Installing " + tool.Name, false);

        // Inspected after the download, right before planning, so the install sees the folder as it is now.
        var inspection = await Task.Run(() => GameToolInstaller.Inspect(tool, target.TargetDirectory, null), token)
            .ConfigureAwait(false);
        var proxy = request.ProxyName ?? GameToolInstaller.SuggestProxy(inspection.Proxies, tool.Proxy.Default);
        var outcome = await _installer.InstallAsync(tool, target, package,
            new GameToolRequest(request.ReplaceExisting ? "replaceExisting" : "install", proxy, request.Settings),
            null, () => _running.ClosedMessage(target), phase => context.Phase(phase, false), token, inspection)
            .ConfigureAwait(false);
        GameToolStorage.RememberIdentity(_userRoot, appId, scan.IsShortcut, scan.Name);
        return outcome.Notice;
    }

    private async Task<string> ReinstallCoreAsync(GameToolDefinition tool, uint appId, string operation,
        Func<GameToolManifest, CancellationToken, Task<HostedRelease>> release, string phase, bool replaceConfig,
        OperationContext context, CancellationToken token)
    {
        var (target, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
        GameToolAdmission.RequireCompleted(manifest);
        await RequireClosedAsync(target).ConfigureAwait(false);
        var package = await Releases.PrepareAsync(tool, await release(manifest, token).ConfigureAwait(false),
            context.Progress, token).ConfigureAwait(false);
        context.Phase(phase, false);
        var outcome = await _installer.InstallAsync(tool, target, package,
            new GameToolRequest(operation, manifest.ProxyName, new Dictionary<string, string>(StringComparer.Ordinal),
                replaceConfig),
            manifest, () => _running.ClosedMessage(target), text => context.Phase(text, false), token)
            .ConfigureAwait(false);
        return outcome.Notice;
    }

    private async Task<HostedRelease> LatestReleaseAsync(GameToolDefinition tool, CancellationToken token)
    {
        var latest = await Releases.LatestAsync(tool, false, token).ConfigureAwait(false);
        return latest.Release ?? throw new GameToolException(latest.Error ?? "No release of " + tool.Name + " is available.");
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
    private Task<SteamUiCommandResult> Start(uint appId, string toolId, string operation,
        Func<GameToolDefinition, string?>? validate,
        Func<GameToolDefinition, OperationContext, CancellationToken, Task<string>> body)
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
            _busy = new GameToolsBusy(appId, operation, "Starting", 0, 0, true);
            _messages[appId] = (null, null);
            var context = new OperationContext(this);
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
                context.Phase("Checking the game folder", false);
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
            if (!force && !Releases.LookupDue(tool))
            {
                continue;
            }

            lock (_sync)
            {
                if (_disposed || !_checking.Add(tool.Id))
                {
                    continue;
                }
            }

            Changed?.Invoke();
            Task.Run(async () =>
            {
                try
                {
                    var latest = await Releases.LatestAsync(tool, force, _lifetime.Token).ConfigureAwait(false);
                    if (force)
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
                        _checking.Remove(tool.Id);
                    }

                    RecomposeOpenGames();
                    Changed?.Invoke();
                }
            }).ObserveFaults();
        }
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
        var details = steam is null ? null : await SteamLaunchConfig.ReadAsync(steam, appId, token).ConfigureAwait(false);
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
                scan = await Task.Run(() => ScanFolder(appId, name, shortcut, root, directTarget, record, layout),
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
        }

        return scan;
    }

    // Recovery runs first in every scan of an existing folder, so the page never shows a crash's half-finished
    // change as current and every mutation, which scans before it acts, starts from a reconciled record.
    private GameToolsScan ScanFolder(uint appId, string name, bool shortcut, string root, string? directTarget,
        GameToolRecord? record, GameFolderLayout? layout)
    {
        var recovery = _recovery.Recover(appId, root, record, Catalog);
        record = recovery.Record;
        layout ??= GameFolderLayout.Scan(root);
        var resolution = GameRuntimeResolver.Resolve(layout, directTarget, record?.Runtime?.RelativePath, name,
            _architectures);
        IReadOnlySet<string> installed = record?.OwnedPaths(root) ?? new HashSet<string>();
        var upscalers = UpscalerScan.Scan(layout, installed);
        var antiCheat = AntiCheatScan.Scan(layout);
        var (availability, reason) = _running.FolderAvailability(appId, layout, resolution.Chosen?.FullPath);
        return new GameToolsScan(appId, name, shortcut, root, availability, reason, resolution, layout.Truncated,
            upscalers, antiCheat, record, layout, Environment.TickCount64, recovery.Notice);
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
                GameToolStatus.ConfidenceLabel(resolution.Confidence));
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
            null, null, null);
    }

    private GameToolsTool ToolState(GameToolDefinition tool, GameToolsScan scan)
    {
        var manifest = scan.Record?.Manifest(tool.Id);
        var latest = Releases.Cached(tool);
        var chosen = scan.Resolution?.Chosen;
        var targetDirectory = manifest is not null && scan.HasFolder
            ? GameToolPaths.Resolve(scan.InstallFolder, manifest.TargetDirectory)
            : chosen is not null ? Path.GetDirectoryName(chosen.FullPath)
            : null;
        var inspection = scan.HasFolder && targetDirectory is not null && Directory.Exists(targetDirectory)
            ? GameToolInstaller.Inspect(tool, targetDirectory, manifest)
            : null;
        var iniValues = manifest is not null && targetDirectory is not null
            ? GameToolInstaller.ReadSettings(tool, targetDirectory)
            : null;
        var evaluation = GameToolStatus.Evaluate(tool, scan.Availability, scan.Reason, chosen?.Architecture, manifest,
            inspection, latest?.Release, scan.Upscalers);
        var proxies = inspection?.Proxies.Select(proxy =>
                          new GameToolsProxyOption(proxy.Name, proxy.Hint, proxy.Available, proxy.Conflict)).ToArray()
                      ?? [.. tool.Proxy.Options.Select(option => new GameToolsProxyOption(option.Name, option.Hint, true, null))];
        var proxyName = manifest?.ProxyName
                        ?? (inspection is null
                            ? tool.Proxy.Default
                            : GameToolInstaller.SuggestProxy(inspection.Proxies, tool.Proxy.Default));
        var settings = tool.Settings.Select(setting =>
        {
            var value = GameToolStatus.SettingValue(setting, manifest, iniValues, _vendors.Value);
            return new GameToolsSetting(setting.Id, setting.Label, setting.Description, value.Value, value.IniValue,
                [.. setting.Options.Select(option => new GameToolsSettingOption(option.Value, option.Label))],
                value.Available, value.Note);
        }).ToArray();
        var relativeTarget = targetDirectory is null
            ? ""
            : GameToolPaths.RelativeDirectory(scan.InstallFolder, targetDirectory);
        return new GameToolsTool(tool.Id, tool.Name, tool.Description, tool.Homepage, tool.License, tool.Notice,
            evaluation.IncompatibleReason, evaluation.Status, evaluation.StatusText, manifest?.Version,
            latest?.Release?.Version, latest?.Error, false, evaluation.UpdateChangesConfig, proxyName,
            manifest is null ? tool.Proxy.Intro : tool.Proxy.RenameIntro, proxies, settings, evaluation.ShowOptions,
            evaluation.CanEditOptions,
            [.. evaluation.Conflicts.Select(conflict => new GameToolsConflict(conflict.RelativePath, conflict.Kind,
                GameToolStatus.KindLabel(conflict.Kind), conflict.Message))],
            evaluation.Warnings, evaluation.Actions,
            new GameToolsFiles(relativeTarget, manifest?.Files.Count ?? 0, manifest?.Replaced.Count ?? 0,
                inspection?.Missing ?? [], inspection?.Modified ?? []));
    }

    private static IReadOnlyList<string> DetectVendors(Func<IReadOnlyList<GpuVendor>> detect)
    {
        try
        {
            return [.. detect().Select(vendor => vendor.ToString().ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
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

    private void Report(string phase, long bytes, long total, bool cancellable)
    {
        bool raise;
        lock (_sync)
        {
            if (_busy is null)
            {
                return;
            }

            var now = Environment.TickCount64;
            raise = _busy.Phase != phase || _busy.Cancellable != cancellable || now - _lastProgress >= ProgressInterval;
            _busy = _busy with { Phase = phase, Bytes = bytes, Total = total, Cancellable = cancellable };
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
            return WindowFinder.ReadRunningPaths(executables);
        }
    }

    /// <summary>Progress reporting handed to one operation.</summary>
    private sealed class OperationContext(GameToolsService owner)
    {
        /// <summary>Reports download and extraction progress; still cancellable.</summary>
        /// <param name="phase">The phase text.</param>
        /// <param name="bytes">Bytes copied.</param>
        /// <param name="total">The expected total.</param>
        internal void Progress(string phase, long bytes, long total)
        {
            owner.Report(phase, bytes, total, true);
        }

        /// <summary>Reports a phase without byte progress.</summary>
        /// <param name="phase">The phase text.</param>
        /// <param name="cancellable">Whether cancel can still stop the work.</param>
        internal void Phase(string phase, bool cancellable)
        {
            owner.Report(phase, 0, 0, cancellable);
        }
    }
}

/// <summary>The expensive part of a game's state: Steam's answer, the record and one folder walk.</summary>
/// <param name="AppId">The game.</param>
/// <param name="Name">The name.</param>
/// <param name="IsShortcut">Whether it is a non-Steam shortcut.</param>
/// <param name="InstallFolder">The current install root, empty when unknown.</param>
/// <param name="Availability">The availability value.</param>
/// <param name="Reason">Why it is not ready.</param>
/// <param name="Resolution">The runtime resolution, or null without a folder.</param>
/// <param name="Truncated">Whether the walk hit its cap.</param>
/// <param name="Upscalers">Recognised upscaler libraries.</param>
/// <param name="AntiCheat">Recognised anti-cheat systems.</param>
/// <param name="Record">The game's durable record as this scan read it, after recovery and rebasing.</param>
/// <param name="Layout">The folder walk, reused by rescans that don't need a new one; null without a folder.</param>
/// <param name="ScannedAt">When the scan finished, in <see cref="Environment.TickCount64" /> milliseconds.</param>
/// <param name="RecoveryNotice">What this scan's recovery undid, published once as the game's notice; null when nothing.</param>
internal sealed record GameToolsScan(
    uint AppId,
    string Name,
    bool IsShortcut,
    string InstallFolder,
    string Availability,
    string? Reason,
    GameRuntimeResolution? Resolution,
    bool Truncated,
    IReadOnlyList<GameUpscalerFile> Upscalers,
    IReadOnlyList<string> AntiCheat,
    GameToolRecord? Record,
    GameFolderLayout? Layout,
    long ScannedAt,
    string? RecoveryNotice = null)
{
    /// <summary>Whether the game has a folder to work in, even while it runs.</summary>
    internal bool HasFolder => GameToolStatus.HasFolder(Availability);

    /// <summary>A scan that found no usable folder.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="name">The name.</param>
    /// <param name="shortcut">Whether it is a shortcut.</param>
    /// <param name="folder">The folder Steam reported, if any.</param>
    /// <param name="availability">The availability value.</param>
    /// <param name="reason">The explanation.</param>
    /// <param name="record">The game's durable record, or null.</param>
    /// <returns>The scan.</returns>
    internal static GameToolsScan Unavailable(uint appId, string name, bool shortcut, string folder,
        string availability, string reason, GameToolRecord? record)
    {
        return new GameToolsScan(appId, name, shortcut, folder, availability, reason, null, false, [], [], record,
            null, Environment.TickCount64);
    }
}
