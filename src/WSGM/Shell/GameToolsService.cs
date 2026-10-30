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
/// <param name="Engine">The detected engine id: <c>unreal</c>, <c>unity</c> or <c>unknown</c>.</param>
/// <param name="EngineLabel">The detected engine shown to the user.</param>
/// <param name="Runtime">The chosen runtime executable, or null when the user must choose.</param>
/// <param name="Candidates">Every executable the game might run, best first.</param>
/// <param name="RememberedMissing">Whether the program the user confirmed earlier is gone.</param>
/// <param name="ScanTruncated">Whether the folder was too large to scan completely.</param>
/// <param name="Upscalers">Standalone upscaler and frame-generation libraries the game ships.</param>
/// <param name="AntiCheat">Names of anti-cheat systems recognised in the folder.</param>
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
    string Engine,
    string EngineLabel,
    GameToolsRuntime? Runtime,
    IReadOnlyList<GameToolsCandidate> Candidates,
    bool RememberedMissing,
    bool ScanTruncated,
    IReadOnlyList<GameToolsUpscaler> Upscalers,
    IReadOnlyList<string> AntiCheat,
    IReadOnlyList<string> GpuVendors,
    IReadOnlyList<GameToolsTool> Tools,
    GameToolsBusy? Busy,
    string? Error,
    string? Notice);

/// <summary>The chosen runtime executable.</summary>
/// <param name="RelativePath">Forward-slash path relative to the install folder, or absolute outside it.</param>
/// <param name="FileName">The file name.</param>
/// <param name="Architecture"><c>x64</c>, <c>x86</c>, <c>arm64</c> or empty.</param>
/// <param name="Confidence"><c>confirmed</c>, <c>structural</c>, <c>single</c> or <c>needsChoice</c>.</param>
/// <param name="Reasons">Evidence, strongest first.</param>
internal sealed record GameToolsRuntime(
    string RelativePath,
    string FileName,
    string Architecture,
    string Confidence,
    IReadOnlyList<string> Reasons);

/// <summary>One executable the user may choose.</summary>
/// <param name="RelativePath">Forward-slash path relative to the install folder, or absolute outside it.</param>
/// <param name="FileName">The file name.</param>
/// <param name="Architecture"><c>x64</c>, <c>x86</c>, <c>arm64</c> or empty.</param>
/// <param name="Engine">The engine id.</param>
/// <param name="Role"><c>game</c>, <c>launcher</c>, <c>helper</c> or <c>unknown</c>.</param>
/// <param name="Reasons">Evidence, strongest first.</param>
/// <param name="Score">The ranking score.</param>
/// <param name="Chosen">Whether it is the current runtime.</param>
internal sealed record GameToolsCandidate(
    string RelativePath,
    string FileName,
    string Architecture,
    string Engine,
    string Role,
    IReadOnlyList<string> Reasons,
    int Score,
    bool Chosen);

/// <summary>A standalone upscaler library the game ships.</summary>
/// <param name="Technology">The technology shown to the user.</param>
/// <param name="Family"><c>dlss</c>, <c>fsr</c>, <c>xess</c> or <c>other</c>.</param>
/// <param name="Kind"><c>upscaler</c>, <c>frameGeneration</c> or <c>other</c>.</param>
/// <param name="RelativePath">Forward-slash path relative to the install folder.</param>
/// <param name="Version">The file version, or null.</param>
internal sealed record GameToolsUpscaler(string Technology, string Family, string Kind, string RelativePath,
    string? Version);

/// <summary>One tool's state for one game.</summary>
/// <param name="Id">The tool id.</param>
/// <param name="Name">The product name.</param>
/// <param name="Description">What it does.</param>
/// <param name="Homepage">The upstream project page.</param>
/// <param name="License">The upstream license identifier.</param>
/// <param name="Notice">The affiliation notice.</param>
/// <param name="Compatible">Whether it can be installed for the chosen runtime.</param>
/// <param name="IncompatibleReason">Why not, or null.</param>
/// <param name="Status"><c>notInstalled</c>, <c>installed</c>, <c>updateAvailable</c>, <c>modified</c>, <c>conflict</c> or <c>unavailable</c>.</param>
/// <param name="StatusText">A short user-facing status line.</param>
/// <param name="InstalledVersion">The installed product version, or null.</param>
/// <param name="LatestVersion">The latest stable release tag, or null when unknown.</param>
/// <param name="LatestError">Why the latest release is unknown, or null.</param>
/// <param name="CheckingLatest">Whether a release lookup is running.</param>
/// <param name="UpdateChangesConfig">Whether the available update changes the configuration format.</param>
/// <param name="ProxyName">The installed proxy name, or the suggested one when not installed.</param>
/// <param name="ProxyOptions">Every offered proxy name.</param>
/// <param name="Settings">The tool's settings.</param>
/// <param name="Conflicts">Files that block installation.</param>
/// <param name="Warnings">Things the user should know before acting.</param>
/// <param name="Actions">Commands currently valid for this tool.</param>
/// <param name="RequiresRiskAcknowledgement">Whether install needs <c>acknowledgeRisk: true</c>.</param>
/// <param name="RiskWarning">The anti-cheat warning text, or null when no anti-cheat was found.</param>
/// <param name="Files">The ownership summary.</param>
internal sealed record GameToolsTool(
    string Id,
    string Name,
    string Description,
    string Homepage,
    string License,
    string Notice,
    bool Compatible,
    string? IncompatibleReason,
    string Status,
    string StatusText,
    string? InstalledVersion,
    string? LatestVersion,
    string? LatestError,
    bool CheckingLatest,
    bool UpdateChangesConfig,
    string ProxyName,
    IReadOnlyList<GameToolsProxyOption> ProxyOptions,
    IReadOnlyList<GameToolsSetting> Settings,
    IReadOnlyList<GameToolsConflict> Conflicts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Actions,
    bool RequiresRiskAcknowledgement,
    string? RiskWarning,
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
/// <param name="Kind"><c>unmanagedTool</c>, <c>otherMod</c>, <c>foreignFile</c> or <c>needsReview</c>.</param>
/// <param name="Message">The explanation.</param>
internal sealed record GameToolsConflict(string Path, string Kind, string Message);

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
/// <param name="ToolId">The tool, empty for game-level work.</param>
/// <param name="Operation"><c>install</c>, <c>replaceExisting</c>, <c>update</c>, <c>repair</c>, <c>configure</c>, <c>remove</c> or <c>checkUpdates</c>.</param>
/// <param name="Phase">The current phase text.</param>
/// <param name="Bytes">Bytes downloaded, zero outside a download.</param>
/// <param name="Total">The expected download size, zero when unknown.</param>
/// <param name="Cancellable">Whether <c>cancel</c> can still stop it.</param>
internal sealed record GameToolsBusy(uint AppId, string ToolId, string Operation, string Phase, long Bytes,
    long Total, bool Cancellable);

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
///     Owns Game Tools for both interfaces: resolving a game's runtime, scanning its folder, and running
///     install, update, repair, configure and remove transactions one at a time.
/// </summary>
/// <remarks>
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
/// </remarks>
internal sealed class GameToolsService : IChangeSource, IDisposable
{
    private const long ProgressInterval = 250;

    private readonly string _userRoot;
    private readonly Func<SteamClient?> _steam;
    private readonly Func<uint, bool?> _appRunning;
    private readonly GameToolReleases _releases;
    private readonly GameToolInstaller _installer;
    private readonly GameToolCatalog _catalog;
    private readonly Lazy<IReadOnlyList<string>> _vendors;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Dictionary<uint, GameToolsScan> _scans = [];
    private readonly Dictionary<uint, GameToolsState> _states = [];
    private readonly Dictionary<uint, (string? Error, string? Notice)> _messages = [];
    private readonly HashSet<uint> _loading = [];
    private readonly HashSet<string> _checking = new(StringComparer.Ordinal);
    private uint _appId;
    private GameToolsBusy? _busy;
    private Task? _operation;
    private CancellationTokenSource? _operationCancellation;
    private long _lastProgress;
    private bool _disposed;

    /// <summary>Creates the service.</summary>
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
        _appRunning = appRunning;
        _catalog = GameToolCatalog.Current;
        _releases = new GameToolReleases(_userRoot, handler);
        _installer = new GameToolInstaller(_userRoot);
        var detect = vendors ?? GpuDriver.DetectVendors;
        _vendors = new Lazy<IReadOnlyList<string>>(() => DetectVendors(detect));
    }

    /// <summary>Raised after state, progress or a result changed, on whatever thread did the work.</summary>
    public event Action? Changed;

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
            _releases.Dispose();
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
        StartRefresh(appId);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Starts a rescan of a game.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Applied; the result follows through <see cref="Changed" />.</returns>
    internal Task<SteamUiCommandResult> RefreshAsync(uint appId, CancellationToken cancellationToken)
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

    /// <summary>The cached state of a game with the current progress, without scanning.</summary>
    /// <param name="appId">The game.</param>
    /// <returns>The state; a loading placeholder before the first scan.</returns>
    internal GameToolsState Peek(uint appId)
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
                    Tools = [.. state.Tools.Select(tool => tool with { CheckingLatest = _checking.Contains(tool.Id) })]
                };
            }

            return Placeholder(appId) with { Busy = busy, Loading = true, Error = messages.Error, Notice = messages.Notice };
        }
    }

    /// <summary>Scans a game and returns its state; the Overlay's read.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="cancellationToken">Cancels waiting for the scan.</param>
    /// <returns>The fresh state, or the cached one while an operation runs.</returns>
    internal async Task<GameToolsState> ReadAsync(uint appId, CancellationToken cancellationToken = default)
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

        StartLatestLookups(appId, false);
        Changed?.Invoke();
        return Peek(appId);
    }

    /// <summary>Looks up the latest release of every tool again.</summary>
    /// <param name="appId">The game whose page asked.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Applied; the result follows through <see cref="Changed" />.</returns>
    internal Task<SteamUiCommandResult> CheckUpdatesAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetMessage(appId, null, null);
        StartLatestLookups(appId, true);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Remembers the program the game runs.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="path">A candidate's relative path, or an absolute path the user picked.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Whether the choice was accepted; the rescan follows through <see cref="Changed" />.</returns>
    internal async Task<SteamUiCommandResult> ChooseRuntimeAsync(uint appId, string path,
        CancellationToken cancellationToken)
    {
        if (IsBusy())
        {
            return new SteamUiCommandResult(false, "Wait for the current Game Tools change to finish.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scan = await ScanAsync(appId, CancellationToken.None).ConfigureAwait(false);
            if (scan.InstallFolder.Length == 0 || scan.Availability is "notInstalled" or "folderUnavailable"
                    or "steamUnavailable")
            {
                return Fail(appId, scan.Reason ?? "The game's folder is not available.");
            }

            var full = Path.IsPathFullyQualified(path)
                ? path
                : Path.Combine(scan.InstallFolder, path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                full = Path.GetFullPath(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Fail(appId, "Choose the game's program.");
            }

            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                return Fail(appId, "Choose the game's program (.exe).");
            }

            if (!StoragePaths.IsUnder(scan.InstallFolder, full) && GameFolderGuard.Reject(full) is { } broad)
            {
                return Fail(appId, broad);
            }

            if (full.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(appId, "Games in the protected WindowsApps folder can't be modified.");
            }

            if (PeImage.TryArchitecture(full) is not { Length: > 0 })
            {
                return Fail(appId, "That file is not a Windows program WSGM can read.");
            }

            var record = GameToolStorage.ReadLocked(_userRoot).Find(appId);
            var relative = GameRuntimeResolver.Relative(scan.InstallFolder, full);
            var installedElsewhere = record?.Tools.FirstOrDefault(manifest =>
                !SameDirectory(TargetDirectory(scan.InstallFolder, manifest), Path.GetDirectoryName(full)!));
            if (installedElsewhere is not null)
            {
                var name = _catalog.Find(installedElsewhere.ToolId)?.Name ?? installedElsewhere.ToolId;
                return Fail(appId, "Remove " + name + " first; it is installed beside the current program.");
            }

            var info = new FileInfo(full);
            GameToolStorage.Mutate(_userRoot, store =>
            {
                var current = store.Find(appId) ?? new GameToolRecord { AppId = appId };
                return store.With(appId, current with
                {
                    IsShortcut = scan.IsShortcut,
                    Name = scan.Name,
                    Runtime = new GameRuntimeChoice
                    {
                        RelativePath = relative, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc
                    }
                });
            });
            Log.Info($"Game Tools: runtime for {appId} set to {relative}.");
            await ScanAsync(appId, CancellationToken.None).ConfigureAwait(false);
            SetMessage(appId, null, "WSGM will use " + Path.GetFileName(full) + ".");
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

    /// <summary>Installs a tool, or replaces an installation made outside WSGM.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    internal Task<SteamUiCommandResult> InstallAsync(GameToolsInstallRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_catalog.Find(request.ToolId) is not { } tool)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Unknown tool."));
        }

        if (ValidateSettings(tool, request.Settings) is { } invalid)
        {
            return Task.FromResult(new SteamUiCommandResult(false, invalid));
        }

        return Start(request.AppId, tool.Id, request.ReplaceExisting ? "replaceExisting" : "install",
            (context, token) => InstallCoreAsync(tool, request, context, token));
    }

    /// <summary>Updates an installed tool to the latest release, keeping its file name and configuration.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="toolId">The tool.</param>
    /// <param name="replaceConfig">Confirms taking the new stock configuration file when the format changes.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    internal Task<SteamUiCommandResult> UpdateAsync(uint appId, string toolId, bool replaceConfig,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _catalog.Find(toolId) is { } tool
            ? Start(appId, tool.Id, "update",
                (context, token) => ReinstallCoreAsync(tool, appId, "update", replaceConfig, context, token))
            : Task.FromResult(new SteamUiCommandResult(false, "Unknown tool."));
    }

    /// <summary>Restores missing or changed files of the installed version.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="toolId">The tool.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    internal Task<SteamUiCommandResult> RepairAsync(uint appId, string toolId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _catalog.Find(toolId) is { } tool
            ? Start(appId, tool.Id, "repair",
                (context, token) => ReinstallCoreAsync(tool, appId, "repair", false, context, token))
            : Task.FromResult(new SteamUiCommandResult(false, "Unknown tool."));
    }

    /// <summary>Renames the installed proxy and changes settings.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="toolId">The tool.</param>
    /// <param name="proxyName">The new proxy name, or null to keep it.</param>
    /// <param name="settings">Changed settings by id.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    internal Task<SteamUiCommandResult> ConfigureAsync(uint appId, string toolId, string? proxyName,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_catalog.Find(toolId) is not { } tool)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Unknown tool."));
        }

        if (ValidateSettings(tool, settings) is { } invalid)
        {
            return Task.FromResult(new SteamUiCommandResult(false, invalid));
        }

        if (proxyName is null && settings.Count == 0)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Nothing to change."));
        }

        return Start(appId, tool.Id, "configure", async (context, _) =>
        {
            var (scan, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
            var target = ManifestTarget(scan, manifest);
            await RequireClosedAsync(appId, target).ConfigureAwait(false);
            context.Phase("Saving settings", false);
            var outcome = await _installer.ConfigureAsync(tool, target, manifest, proxyName, settings,
                _ => Task.FromResult(ClosedMessage(appId, target))).ConfigureAwait(false);
            return outcome.Notice;
        });
    }

    /// <summary>Removes a tool and restores the game files it replaced.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="toolId">The tool.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Whether the work was admitted.</returns>
    internal Task<SteamUiCommandResult> RemoveAsync(uint appId, string toolId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_catalog.Find(toolId) is not { } tool)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Unknown tool."));
        }

        return Start(appId, tool.Id, "remove", async (context, _) =>
        {
            var (scan, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
            var target = ManifestTarget(scan, manifest);
            await RequireClosedAsync(appId, target).ConfigureAwait(false);
            context.Phase("Removing " + tool.Name, false);
            var outcome = await Task.Run(() => _installer.Remove(tool, target, manifest), CancellationToken.None)
                .ConfigureAwait(false);
            return outcome.Notice;
        });
    }

    /// <summary>Cancels a running download for a game.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="cancellationToken">The bridge request's cancellation.</param>
    /// <returns>Applied when something was cancelled.</returns>
    internal Task<SteamUiCommandResult> CancelAsync(uint appId, CancellationToken cancellationToken)
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
        RequireReady(scan);
        var record = GameToolStorage.ReadLocked(_userRoot).Find(appId);
        if (record?.Tools.FirstOrDefault(manifest => manifest.ToolId == tool.Id) is not null)
        {
            throw new GameToolException(tool.Name + " is already installed. Use Update or Repair.");
        }

        if (scan.Resolution?.Chosen is not { } runtime)
        {
            throw new GameToolException("Choose the game's program first.");
        }

        if (scan.AntiCheat.Count > 0 && !request.AcknowledgeRisk)
        {
            throw new GameToolException(tool.AntiCheatWarning + " Confirm the warning to continue.");
        }

        if (!tool.Architectures.Contains(runtime.Architecture, StringComparer.Ordinal))
        {
            throw new GameToolException(Incompatible(tool, runtime.Architecture)!);
        }

        var target = new GameToolTarget(appId, scan.InstallFolder, runtime.FullPath, runtime.Architecture, scan.Engine);
        var inspection = GameToolInstaller.Inspect(tool, target.TargetDirectory, null);
        var proxy = request.ProxyName ?? GameToolInstaller.SuggestProxy(inspection.Proxies, tool.Proxy.Default);
        await RequireClosedAsync(appId, target).ConfigureAwait(false);
        var latest = await _releases.LatestAsync(tool, false, token).ConfigureAwait(false);
        if (latest.Release is not { } release)
        {
            throw new GameToolException(latest.Error ?? "No release of " + tool.Name + " is available.");
        }

        var package = await _releases.PrepareAsync(tool, release, context.Progress, token).ConfigureAwait(false);
        context.Phase("Installing " + tool.Name, false);
        var outcome = await _installer.InstallAsync(tool, target, package,
            new GameToolRequest(request.ReplaceExisting ? "replace" : "install", proxy, request.Settings,
                request.ReplaceExisting),
            null, _ => Task.FromResult(ClosedMessage(appId, target)), phase => context.Phase(phase, false),
            token).ConfigureAwait(false);
        RememberIdentity(appId, scan);
        return outcome.Notice;
    }

    private async Task<string> ReinstallCoreAsync(GameToolDefinition tool, uint appId, string operation,
        bool replaceConfig, OperationContext context, CancellationToken token)
    {
        var (scan, manifest) = await InstalledAsync(tool, appId).ConfigureAwait(false);
        if (manifest.State != GameToolManifestState.Installed)
        {
            throw new GameToolException("An earlier change was interrupted. Use Remove to finish undoing it.");
        }

        var target = ManifestTarget(scan, manifest);
        await RequireClosedAsync(appId, target).ConfigureAwait(false);
        EmulatorRelease release;
        if (operation == "update")
        {
            var latest = await _releases.LatestAsync(tool, false, token).ConfigureAwait(false);
            release = latest.Release
                      ?? throw new GameToolException(latest.Error ?? "No release of " + tool.Name + " is available.");
        }
        else
        {
            release = GameToolReleases.FromManifest(manifest);
        }

        var package = await _releases.PrepareAsync(tool, release, context.Progress, token).ConfigureAwait(false);
        context.Phase(operation == "update" ? "Updating " + tool.Name : "Repairing " + tool.Name, false);
        var outcome = await _installer.InstallAsync(tool, target, package,
            new GameToolRequest(operation, manifest.ProxyName, new Dictionary<string, string>(StringComparer.Ordinal),
                ReplaceConfig: replaceConfig),
            manifest, _ => Task.FromResult(ClosedMessage(appId, target)), phase => context.Phase(phase, false),
            token).ConfigureAwait(false);
        return outcome.Notice;
    }

    private async Task<(GameToolsScan Scan, GameToolManifest Manifest)> InstalledAsync(GameToolDefinition tool,
        uint appId)
    {
        var scan = await ScanAsync(appId, CancellationToken.None).ConfigureAwait(false);
        if (scan.Availability is "notInstalled" or "folderUnavailable" or "steamUnavailable")
        {
            throw new GameToolException(scan.Reason ?? "The game's folder is not available.");
        }

        var manifest = GameToolStorage.ReadLocked(_userRoot).Find(appId)?.Tools
                           .FirstOrDefault(item => item.ToolId == tool.Id)
                       ?? throw new GameToolException(tool.Name + " is not installed by WSGM for this game.");
        return (scan, manifest);
    }

    private static GameToolTarget ManifestTarget(GameToolsScan scan, GameToolManifest manifest)
    {
        var runtime = Path.IsPathFullyQualified(manifest.RuntimeExecutable)
            ? manifest.RuntimeExecutable
            : Path.Combine(scan.InstallFolder, manifest.RuntimeExecutable.Replace('/', Path.DirectorySeparatorChar));
        return new GameToolTarget(scan.AppId, scan.InstallFolder, runtime, manifest.Architecture, manifest.Engine);
    }

    private static string TargetDirectory(string root, GameToolManifest manifest)
    {
        return Path.IsPathFullyQualified(manifest.TargetDirectory)
            ? manifest.TargetDirectory
            : Path.Combine(root, manifest.TargetDirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void RequireReady(GameToolsScan scan)
    {
        if (scan.Availability != "ready")
        {
            throw new GameToolException(scan.Reason ?? "The game is not available.");
        }
    }

    private async Task RequireClosedAsync(uint appId, GameToolTarget target)
    {
        if (await Task.Run(() => ClosedMessage(appId, target)).ConfigureAwait(false) is { } running)
        {
            throw new GameToolException(running);
        }
    }

    // Fails closed: an unknown answer is treated as running, and the check runs again right before the
    // first game-file write.
    private string? ClosedMessage(uint appId, GameToolTarget target)
    {
        if (_appRunning(appId) == true)
        {
            return "Close the game first.";
        }

        return RunningPaths(target.InstallRoot, target.TargetDirectory, target.RuntimeExecutable) switch
        {
            true => "Close the game first.",
            null => "WSGM couldn't confirm that the game is closed. Close it and try again.",
            _ => null
        };
    }

    private static bool? RunningPaths(string root, string? directory, string? runtime)
    {
        List<string> paths = [];
        if (runtime is not null && File.Exists(runtime))
        {
            paths.Add(runtime);
        }

        foreach (var folder in new[] { directory, root }.Where(folder => folder is not null && Directory.Exists(folder)))
        {
            try
            {
                paths.AddRange(Directory.EnumerateFiles(folder!, "*.exe", SearchOption.TopDirectoryOnly).Take(64));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (paths.Count == 0)
        {
            return false;
        }

        var running = WindowFinder.ReadRunningPaths(paths);
        return running.Values.Any(value => value == true) ? true
            : running.Values.Any(value => value is null) ? null
            : false;
    }

    private Task<SteamUiCommandResult> Start(uint appId, string toolId, string operation,
        Func<OperationContext, CancellationToken, Task<string>> body)
    {
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
            _busy = new GameToolsBusy(appId, toolId, operation, "Starting", 0, 0, true);
            _messages[appId] = (null, null);
            var context = new OperationContext(this, appId);
            _operation = Task.Run(() => RunAsync(appId, toolId, operation, context, body, cancellation));
            _operation.ObserveFaults();
        }

        Changed?.Invoke();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private async Task RunAsync(uint appId, string toolId, string operation, OperationContext context,
        Func<OperationContext, CancellationToken, Task<string>> body, CancellationTokenSource cancellation)
    {
        string? error = null;
        string? notice = null;
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            entered = true;
            notice = await body(context, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            notice = "Cancelled. Nothing in the game folder was changed.";
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
                context.Phase("Checking the game folder", false);
                await ScanAsync(appId, CancellationToken.None).ConfigureAwait(false);
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
        foreach (var tool in _catalog.Tools)
        {
            if (!force && _releases.Cached(tool) is { Release: not null })
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
                    var latest = await _releases.LatestAsync(tool, force, _lifetime.Token).ConfigureAwait(false);
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
                        foreach (var cached in _scans.Keys.ToArray())
                        {
                            Compose(cached);
                        }
                    }

                    Changed?.Invoke();
                }
            }).ObserveFaults();
        }
    }

    private async Task<GameToolsScan> ScanAsync(uint appId, CancellationToken token)
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
                "Steam isn't reachable or did not report this game.");
        }
        else
        {
            var (root, directTarget, reason) = InstallRoot(shortcut, details);
            if (root.Length == 0)
            {
                scan = GameToolsScan.Unavailable(appId, name, shortcut, "", "notInstalled", reason);
            }
            else if (!Directory.Exists(root))
            {
                scan = GameToolsScan.Unavailable(appId, name, shortcut, root, "folderUnavailable",
                    "The game's folder isn't available. Connect the drive or library it is on.");
            }
            else
            {
                scan = await Task.Run(() => ScanFolder(appId, name, shortcut, root, directTarget), token)
                    .ConfigureAwait(false);
            }
        }

        lock (_sync)
        {
            _scans[appId] = scan;
            Compose(appId);
        }

        return scan;
    }

    private static (string Root, string? DirectTarget, string Reason) InstallRoot(bool shortcut,
        SteamAppDetails details)
    {
        if (!shortcut)
        {
            var folder = Clean(details.InstallFolder);
            return folder.Length > 0 && Path.IsPathFullyQualified(folder)
                ? (Path.GetFullPath(folder), null, "")
                : ("", null, "This game isn't installed.");
        }

        var target = Clean(SteamLaunchConfig.OriginalsFrom(true, details).Target);
        if (target.Length == 0 || !Path.IsPathFullyQualified(target)
                               || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("", null, "This shortcut doesn't start a Windows program WSGM can modify.");
        }

        target = Path.GetFullPath(target);
        var start = Clean(details.ShortcutStartDir);
        var targetFolder = Path.GetDirectoryName(target) ?? "";
        // The start folder is the game root only when it contains the program and is not too broad.
        if (start.Length > 0 && Path.IsPathFullyQualified(start) && StoragePaths.IsUnder(start, target)
            && GameFolderGuard.Reject(Path.Combine(start, "x.exe")) is null)
        {
            return (Path.GetFullPath(start), target, "");
        }

        // The program's own folder is the fallback, and it gets the same guard as a browsed program:
        // Windows, the profile, Downloads and other shared folders are never a game folder.
        return GameFolderGuard.Reject(target) is null
            ? (targetFolder, target, "")
            : ("", null, "This shortcut starts a program in " + targetFolder + ", a folder other programs "
                         + "share. WSGM only adds files to a game's own folder.");
    }

    private static string Clean(string? path)
    {
        return (path ?? "").Trim().Trim('"').Trim();
    }

    private GameToolsScan ScanFolder(uint appId, string name, bool shortcut, string root, string? directTarget)
    {
        var record = GameToolStorage.ReadLocked(_userRoot).Find(appId);
        record = Reconcile(appId, root, record);
        var layout = GameFolderLayout.Scan(root);
        var resolution = GameRuntimeResolver.Resolve(layout, directTarget, record?.Runtime?.RelativePath, name);
        HashSet<string> installed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in record?.Tools ?? [])
        {
            var directory = TargetDirectory(root, manifest);
            foreach (var file in manifest.Files)
            {
                var full = Path.Combine(directory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (StoragePaths.IsUnder(root, full))
                {
                    installed.Add(GameToolPattern.Normalize(Path.GetRelativePath(root, full)));
                }
            }
        }

        var upscalers = UpscalerScan.Scan(layout, installed);
        var antiCheat = AntiCheatScan.Scan(layout);
        var runtimePath = resolution.Chosen?.FullPath;
        var running = _appRunning(appId) == true
                      || RunningPaths(root, runtimePath is null ? null : Path.GetDirectoryName(runtimePath), runtimePath)
                      == true;
        return new GameToolsScan(appId, name, shortcut, root, running ? "running" : "ready",
            running ? "Close the game to change its tools." : null, resolution, layout.Truncated, upscalers, antiCheat,
            resolution.Engine);
    }

    // Rolls back transactions a crash left pending and rebases manifests of a game that moved library.
    private GameToolRecord? Reconcile(uint appId, string root, GameToolRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        var changed = false;
        foreach (var manifest in record.Tools.Where(manifest => manifest.State == GameToolManifestState.Pending))
        {
            // Rolling back writes game files, so it waits until the game is known to be closed; until then
            // the change stays pending and the page shows it as interrupted.
            var directory = TargetDirectory(root, manifest);
            var runtime = Path.IsPathFullyQualified(manifest.RuntimeExecutable)
                ? manifest.RuntimeExecutable
                : Path.Combine(root, manifest.RuntimeExecutable.Replace('/', Path.DirectorySeparatorChar));
            if (_appRunning(appId) == true || RunningPaths(root, directory, runtime) != false)
            {
                Log.Info($"Game Tools: interrupted {manifest.Operation} of {manifest.ToolId} for {appId} waits "
                         + "until the game is closed.");
                continue;
            }

            Log.Warn($"Game Tools: rolling back an interrupted {manifest.Operation} of {manifest.ToolId} for {appId}.");
            _installer.Rollback(appId, directory, manifest);
            changed = true;
        }

        if (changed)
        {
            record = GameToolStorage.ReadLocked(_userRoot).Find(appId);
        }

        if (record?.Tools.Any(manifest => !SameDirectory(manifest.InstallRoot, root)) == true)
        {
            Log.Info($"Game Tools: game {appId} moved to {root}; rebasing its manifests.");
            record = GameToolStorage.Mutate(_userRoot, store =>
            {
                var current = store.Find(appId);
                return current is null
                    ? store
                    : store.With(appId, current with
                    {
                        Tools = [.. current.Tools.Select(manifest => manifest with { InstallRoot = root })]
                    });
            }).Find(appId);
        }

        return record;
    }

    private void RememberIdentity(uint appId, GameToolsScan scan)
    {
        GameToolStorage.Mutate(_userRoot, store => store.Find(appId) is { } record
            ? store.With(appId, record with { IsShortcut = scan.IsShortcut, Name = scan.Name })
            : store);
    }

    // Builds the published state from the cached scan and the current record; cheap, no folder walk.
    // Callers hold _sync.
    private void Compose(uint appId)
    {
        if (!_scans.TryGetValue(appId, out var scan))
        {
            return;
        }

        GameToolRecord? record;
        try
        {
            record = GameToolStorage.ReadLocked(_userRoot).Find(appId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException)
        {
            Log.Warn($"Game Tools: state file unreadable: {ex.Message}");
            record = null;
        }

        var resolution = scan.Resolution;
        var chosen = resolution?.Chosen;
        var runtime = chosen is null
            ? null
            : new GameToolsRuntime(chosen.RelativePath, chosen.FileName, chosen.Architecture,
                Confidence(resolution!.Confidence), chosen.Reasons);
        var candidates = resolution?.Candidates.Select(candidate => new GameToolsCandidate(candidate.RelativePath,
            candidate.FileName, candidate.Architecture, candidate.Engine, Role(candidate.Role), candidate.Reasons,
            candidate.Score, ReferenceEquals(candidate, chosen))).ToArray() ?? [];
        var tools = _catalog.Tools.Select(tool => ToolState(tool, scan, record)).ToArray();
        _states[appId] = new GameToolsState(appId, scan.Name, scan.IsShortcut, scan.InstallFolder, scan.Availability,
            scan.Reason, false, scan.Engine, resolution?.EngineLabel ?? "Unknown engine", runtime, candidates,
            resolution?.RememberedMissing == true, scan.Truncated,
            [.. scan.Upscalers.Select(file => new GameToolsUpscaler(file.Technology, file.Family, file.Kind,
                file.RelativePath, file.Version))],
            scan.AntiCheat, _vendors.Value, tools, null, null, null);
    }

    private GameToolsTool ToolState(GameToolDefinition tool, GameToolsScan scan, GameToolRecord? record)
    {
        var manifest = record?.Tools.FirstOrDefault(item => item.ToolId == tool.Id);
        var latest = _releases.Cached(tool);
        var chosen = scan.Resolution?.Chosen;
        var folderReady = scan.Availability is "ready" or "running";
        var incompatible = chosen is null ? "Choose the game's program first." : Incompatible(tool, chosen.Architecture);
        var targetDirectory = manifest is not null && folderReady ? TargetDirectory(scan.InstallFolder, manifest)
            : chosen is not null ? Path.GetDirectoryName(chosen.FullPath)
            : null;
        var inspection = folderReady && targetDirectory is not null && Directory.Exists(targetDirectory)
            ? GameToolInstaller.Inspect(tool, targetDirectory, manifest)
            : null;
        List<GameToolsConflict> conflicts = [];
        List<string> warnings = [];
        List<string> actions = [];
        var iniValues = manifest is not null && targetDirectory is not null
            ? GameToolInstaller.ReadSettings(tool, targetDirectory)
            : null;
        var relation = manifest is null
            ? GameToolVersionRelation.Unknown
            : GameToolVersions.Relate(tool,
                manifest.ReleaseVersion.Length > 0 ? manifest.ReleaseVersion : manifest.Version, manifest.ReleaseId,
                latest?.Release?.Version, latest?.Release?.Id);
        var latestFamily = latest?.Release is { } release ? GameToolVersions.Family(tool, release.Version) : "";
        var changesConfig = manifest is not null && relation == GameToolVersionRelation.UpdateAvailable
                                                 && manifest.Family.Length > 0 && latestFamily.Length > 0
                                                 && manifest.Family != latestFamily;
        string status;
        string statusText;
        if (!folderReady)
        {
            status = "unavailable";
            statusText = scan.Reason ?? "Not available.";
        }
        else if (manifest is not null)
        {
            if (manifest.State != GameToolManifestState.Installed)
            {
                status = "modified";
                statusText = "An earlier change was interrupted. Remove to finish undoing it.";
                actions.Add("remove");
            }
            else if (inspection is null)
            {
                status = "modified";
                statusText = "The folder " + tool.Name + " was installed into isn't available. If it is on a drive "
                             + "that isn't connected, connect it first.";
                actions.Add("remove");
            }
            else if (inspection.Missing.Count > 0 || inspection.Modified.Count > 0)
            {
                status = "modified";
                statusText = "Some of " + tool.Name + "'s files are missing or were changed.";
                actions.AddRange(["repair", "remove"]);
            }
            else
            {
                status = relation == GameToolVersionRelation.UpdateAvailable ? "updateAvailable" : "installed";
                statusText = relation switch
                {
                    GameToolVersionRelation.UpdateAvailable => "Version " + latest!.Release!.Version + " is available.",
                    GameToolVersionRelation.NewerThanLatest => "Newer than the latest stable release.",
                    _ => "Installed as " + manifest.ProxyName + "."
                };
                if (relation == GameToolVersionRelation.UpdateAvailable)
                {
                    actions.Add("update");
                }

                actions.AddRange(["configure", "repair", "remove"]);
            }

            foreach (var copy in inspection?.Unmanaged ?? [])
            {
                conflicts.Add(new GameToolsConflict(copy.RelativePath, "needsReview",
                    "Another copy of " + tool.Name + " (" + copy.Version + ") is in the folder."));
            }
        }
        else if (inspection is { Unmanaged.Count: > 0 })
        {
            status = "conflict";
            var versions = inspection.Unmanaged.Select(copy => copy.Version).Distinct(StringComparer.Ordinal).Count();
            statusText = tool.Name + " was installed outside WSGM.";
            foreach (var copy in inspection.Unmanaged)
            {
                conflicts.Add(new GameToolsConflict(copy.RelativePath, versions > 1 ? "needsReview" : "unmanagedTool",
                    versions > 1
                        ? "Several copies of " + tool.Name + " with different versions are installed."
                        : tool.Name + " " + copy.Version + " installed outside WSGM."));
            }

            if (incompatible is null)
            {
                actions.Add("replaceExisting");
            }
        }
        else
        {
            status = "notInstalled";
            statusText = incompatible ?? "Not installed.";
            if (incompatible is null)
            {
                actions.Add("install");
            }
        }

        if (manifest is null && folderReady && scan.Upscalers.All(file =>
                !tool.RequiresUpscaler.Contains(file.Family, StringComparer.Ordinal)))
        {
            warnings.Add("No standalone DLSS, FSR or XeSS library was found. The game may still support one "
                         + "built in; " + tool.Name + " only works when the game offers an upscaler.");
        }

        if (changesConfig)
        {
            warnings.Add("This update changes " + tool.PrimaryConfig + "'s format. Updating replaces it with the "
                         + "new default file and applies WSGM's settings again.");
        }

        if (scan.AntiCheat.Count > 0)
        {
            warnings.Add(tool.AntiCheatWarning + " Found: " + string.Join(", ", scan.AntiCheat) + ".");
        }

        if (scan.Availability == "running")
        {
            actions.Clear();
        }

        var proxies = inspection?.Proxies.Select(proxy =>
                          new GameToolsProxyOption(proxy.Name, proxy.Hint, proxy.Available, proxy.Conflict)).ToArray()
                      ?? [.. tool.Proxy.Options.Select(option => new GameToolsProxyOption(option.Name, option.Hint, true, null))];
        var proxyName = manifest?.ProxyName
                        ?? (inspection is null
                            ? tool.Proxy.Default
                            : GameToolInstaller.SuggestProxy(inspection.Proxies, tool.Proxy.Default));
        var settings = tool.Settings.Select(setting => SettingState(setting, manifest, iniValues)).ToArray();
        var relativeTarget = targetDirectory is null ? "" : RelativeDirectory(scan.InstallFolder, targetDirectory);
        return new GameToolsTool(tool.Id, tool.Name, tool.Description, tool.Homepage, tool.License, tool.Notice,
            incompatible is null, incompatible, status, statusText, manifest?.Version, latest?.Release?.Version,
            latest?.Error, false, changesConfig, proxyName, proxies, settings, conflicts, warnings, actions,
            scan.AntiCheat.Count > 0, scan.AntiCheat.Count > 0 ? tool.AntiCheatWarning : null,
            new GameToolsFiles(relativeTarget, manifest?.Files.Count ?? 0, manifest?.Replaced.Count ?? 0,
                inspection?.Missing ?? [], inspection?.Modified ?? []));
    }

    private GameToolsSetting SettingState(GameToolSetting setting, GameToolManifest? manifest,
        IReadOnlyDictionary<string, string>? iniValues)
    {
        var options = setting.Options.Select(option => new GameToolsSettingOption(option.Value, option.Label)).ToArray();
        var notes = _vendors.Value.Select(vendor => setting.VendorNotes.GetValueOrDefault(vendor))
            .Where(note => note is not null).ToArray();
        var note = notes.Length > 0 ? string.Join(" ", notes) : null;
        if (manifest is null)
        {
            return new GameToolsSetting(setting.Id, setting.Label, setting.Description, setting.Default, null, options,
                true, note);
        }

        if (iniValues is null || !iniValues.TryGetValue(setting.Id, out var raw))
        {
            return new GameToolsSetting(setting.Id, setting.Label, setting.Description,
                manifest.Settings.GetValueOrDefault(setting.Id) ?? setting.Default, null, options, false,
                "Not available in the installed version.");
        }

        var matched = setting.Options.FirstOrDefault(option =>
            option.Ini.Equals(raw, StringComparison.OrdinalIgnoreCase))?.Value;
        return new GameToolsSetting(setting.Id, setting.Label, setting.Description, matched ?? "", raw, options, true,
            matched is null ? "Set to \"" + raw + "\" in the configuration file." : note);
    }

    private static string? ValidateSettings(GameToolDefinition tool, IReadOnlyDictionary<string, string> settings)
    {
        foreach (var (id, value) in settings)
        {
            var setting = tool.Settings.FirstOrDefault(item => item.Id == id);
            if (setting is null || setting.Options.All(option => option.Value != value))
            {
                return "Unknown setting or value: " + id + ".";
            }
        }

        return null;
    }

    private static string? Incompatible(GameToolDefinition tool, string architecture)
    {
        if (tool.Architectures.Contains(architecture, StringComparer.Ordinal))
        {
            return null;
        }

        var kind = architecture switch
        {
            PeImage.X86 => "32-bit games",
            PeImage.Arm64 => "ARM64 games",
            PeImage.X64 => "64-bit games",
            _ => "this game's program"
        };
        return tool.Name + " doesn't support " + kind + ".";
    }

    private static string Confidence(GameRuntimeConfidence confidence)
    {
        return confidence switch
        {
            GameRuntimeConfidence.Confirmed => "confirmed",
            GameRuntimeConfidence.Structural => "structural",
            GameRuntimeConfidence.Single => "single",
            _ => "needsChoice"
        };
    }

    private static string Role(GameRuntimeRole role)
    {
        return role switch
        {
            GameRuntimeRole.Game => "game",
            GameRuntimeRole.Launcher => "launcher",
            GameRuntimeRole.Helper => "helper",
            _ => "unknown"
        };
    }

    private static string RelativeDirectory(string root, string directory)
    {
        if (!StoragePaths.IsUnder(root, directory) && !SameDirectory(root, directory))
        {
            return directory;
        }

        var relative = GameToolPattern.Normalize(Path.GetRelativePath(root, directory));
        return relative == "." ? "" : relative;
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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

    private GameToolsState Placeholder(uint appId)
    {
        return new GameToolsState(appId, appId.ToString(CultureInfo.InvariantCulture), SteamApps.IsShortcutAppId(appId),
            "", "ready", null, true, "unknown", "Unknown engine", null, [], false, false, [], [], [], [], null, null,
            null);
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
                "WSGM couldn't read this game's folder. Refresh to try again.");
            Compose(appId);
        }
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

    /// <summary>Progress reporting handed to one operation.</summary>
    private sealed class OperationContext(GameToolsService owner, uint appId)
    {
        /// <summary>The game the operation runs for.</summary>
        internal uint AppId { get; } = appId;

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

/// <summary>The expensive part of a game's state: Steam's answer and one folder walk.</summary>
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
/// <param name="Engine">The engine id.</param>
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
    string Engine)
{
    /// <summary>A scan that found no usable folder.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="name">The name.</param>
    /// <param name="shortcut">Whether it is a shortcut.</param>
    /// <param name="folder">The folder Steam reported, if any.</param>
    /// <param name="availability">The availability value.</param>
    /// <param name="reason">The explanation.</param>
    /// <returns>The scan.</returns>
    internal static GameToolsScan Unavailable(uint appId, string name, bool shortcut, string folder,
        string availability, string reason)
    {
        return new GameToolsScan(appId, name, shortcut, folder, availability, reason, null, false, [], [], "unknown");
    }
}
