using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Owns serialized launch writes and their durable restoration records for both interfaces.</summary>
internal sealed class LaunchActionService : IChangeSource
{
    private readonly ConfigStore _store;
    private readonly Func<SteamClient?> _steam;
    private readonly Func<bool> _shimManaged;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private uint _appId;
    private string? _notice;
    private uint _noticeAppId;

    internal LaunchActionService(ConfigStore store, Func<SteamClient?> steam, Func<bool> shimManaged)
    {
        _store = store;
        _steam = steam;
        _shimManaged = shimManaged;
    }

    /// <summary>Announces writes to both projections; subsequent reads always consult Steam.</summary>
    public event Action? Changed;

    /// <summary>Opens the exact game supplied by the cog menu.</summary>
    internal async Task<SteamUiCommandResult> OpenAsync(uint appId, CancellationToken token)
    {
        if (appId == 0)
        {
            return new SteamUiCommandResult(false, "No game was supplied.");
        }

        _appId = appId;
        _notice = null;
        _noticeAppId = appId;
        var state = await ReadAsync(appId, token).ConfigureAwait(false);
        Changed?.Invoke();
        return new SteamUiCommandResult(state.Error is null, state.Error);
    }

    /// <summary>Refreshes the Steam projection, including changes made outside WSGM.</summary>
    internal async ValueTask<LaunchActionState?> ReadStateAsync()
    {
        var appId = _appId;
        return appId == 0 ? null : await ReadAsync(appId).ConfigureAwait(false);
    }

    /// <summary>Reads current fields rather than treating a recovery snapshot as proof of ownership.</summary>
    internal async Task<LaunchActionState> ReadAsync(uint appId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var shortcut = SteamApps.IsShortcutAppId(appId);
            var shim = _shimManaged();
            var available = File.Exists(LaunchWrapperCommand.HelperPathForCurrentDeployment());
            var details = _steam() is { } steam
                ? await SteamLaunchConfig.ReadAsync(steam, appId, token).ConfigureAwait(false)
                : null;
            var snapshot = await FindSnapshotAsync(appId, token).ConfigureAwait(false);
            var canRestore = snapshot is not null && (!shortcut || !string.IsNullOrWhiteSpace(snapshot.OriginalTarget));
            var mode = details is null ? LaunchWrapperMode.None : LaunchWrapperCommand.ModeFor(shortcut, details);
            var custom = details is not null && snapshot?.Kind == LaunchConfigurationKind.CustomAction
                && MatchesCustom(shortcut, details, snapshot);
            var ownership = details is null ? "Unknown" : custom ? "Custom launch action" : Label(mode);
            var changed = details is not null && snapshot is not null
                && !MatchesSnapshot(shortcut, details, snapshot);
            var route = shim
                ? "Steam Input Management enabled. Applying a lease selects Steam's resident shim."
                : "Steam Input Management disabled. Applying a lease selects the existing injecting route.";
            if (mode.HasFlag(LaunchWrapperMode.InputLease))
            {
                route += " This game is configured for the resident shim; its lease fails open when the shim is unavailable.";
                if (!shim)
                {
                    route += " Apply it again to select the current route.";
                }
            }
            else if (mode.HasFlag(LaunchWrapperMode.InputLeaseInject))
            {
                route += " This game is configured for the injecting lease route.";
            }

            return new LaunchActionState(appId, details?.Name ?? snapshot?.Name ?? appId.ToString(CultureInfo.InvariantCulture),
                shortcut, ownership, canRestore, available, shim, changed,
                details?.ShortcutExe ?? "", details is null ? "" : shortcut ? details.ShortcutLaunchOptions : details.LaunchOptions,
                route, details is null ? "Steam isn't reachable or did not report this game." : null,
                appId == _noticeAppId ? _notice : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Could not read launch actions for {appId}", ex);
            return new LaunchActionState(appId, appId.ToString(CultureInfo.InvariantCulture), SteamApps.IsShortcutAppId(appId),
                "Unknown", false, false, _shimManaged(), false, "", "", "", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Builds the clipboard fallback through the same current input route and deployment validation.</summary>
    internal string ClipboardCommand(LaunchWrapperMode mode)
    {
        var helper = LaunchWrapperCommand.HelperPathForCurrentDeployment();
        if (!File.Exists(helper))
        {
            throw new FileNotFoundException("Launch wrapper missing", helper);
        }

        return LaunchWrapperCommand.SteamLaunchOptions(helper,
            LaunchWrapperCommand.ForCurrentInputMode(mode, _shimManaged()));
    }

    /// <summary>Applies the existing wrapper route or restores the first captured original.</summary>
    internal Task<LaunchConfigResult> ApplyAsync(uint appId, LaunchWrapperMode mode, CancellationToken token = default)
    {
        return ChangeAsync(appId, mode, null, "", token);
    }

    /// <summary>Revalidates a picker selection before replacing Steam's native launch fields.</summary>
    internal Task<LaunchConfigResult> ApplyCustomAsync(uint appId, string path, string arguments,
        CancellationToken token = default)
    {
        return ChangeAsync(appId, LaunchWrapperMode.None, path, arguments, token);
    }

    private async Task<LaunchConfigResult> ChangeAsync(uint appId, LaunchWrapperMode mode, string? path,
        string arguments, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (appId == 0 || _steam() is not { } steam)
            {
                return new LaunchConfigResult(false, "Steam isn't reachable.");
            }

            if (path is not null && (!Path.IsPathFullyQualified(path) || !SteamCustomLaunchCommand.IsSupported(path)))
            {
                return new LaunchConfigResult(false, "Choose an available executable or script (.exe, .cmd, .bat or .ps1).");
            }

            // Validate arguments before persisting a new snapshot.
            if (path is not null)
            {
                _ = SteamCustomLaunchCommand.Build(path, arguments);
            }

            var shortcut = SteamApps.IsShortcutAppId(appId);
            var current = await SteamLaunchConfig.ReadAsync(steam, appId, token).ConfigureAwait(false);
            if (current is null)
            {
                return new LaunchConfigResult(false, "Steam didn't answer.");
            }

            var existing = await FindSnapshotAsync(appId, token).ConfigureAwait(false);
            LaunchConfigResult result;
            if (mode == LaunchWrapperMode.None && path is null)
            {
                if (existing is null)
                {
                    return new LaunchConfigResult(false, "No original launch action has been recorded for this game.");
                }

                result = await SteamLaunchConfig.RestoreAsync(steam, existing, token).ConfigureAwait(false);
                if (result.Confirmed)
                {
                    await ForgetSnapshotAsync(appId).ConfigureAwait(false);
                }
            }
            else
            {
                if (path is not null && !File.Exists(path))
                {
                    return new LaunchConfigResult(false, "The selected launch action is no longer available.");
                }

                if (path is null && !File.Exists(LaunchWrapperCommand.HelperPathForCurrentDeployment()))
                {
                    return new LaunchConfigResult(false, "The launch wrapper is missing from this install.");
                }

                mode = LaunchWrapperCommand.ForCurrentInputMode(mode, _shimManaged());
                var snapshot = CaptureSnapshot(appId, shortcut, current, existing);
                if (snapshot is null)
                {
                    return new LaunchConfigResult(false, "Can't read the original program. The launch action was left unchanged.");
                }

                // Switching from our custom action must wrap the original game, not the custom action.
                var baseline = existing?.Kind == LaunchConfigurationKind.CustomAction && MatchesCustom(shortcut, current, existing)
                    ? current with
                    {
                        ShortcutExe = existing.OriginalTarget,
                        ShortcutLaunchOptions = existing.OriginalLaunchOptions,
                        LaunchOptions = existing.OriginalLaunchOptions
                    }
                    : current;
                snapshot.Kind = path is null ? LaunchConfigurationKind.Wrapper : LaunchConfigurationKind.CustomAction;
                snapshot.Mode = mode;
                snapshot.CustomActionPath = path ?? "";
                snapshot.CustomArguments = arguments;
                snapshot.Name = current.Name ?? existing?.Name ?? appId.ToString(CultureInfo.InvariantCulture);
                // Persist BEFORE the write; on an uncertain answer this is the only recoverable original.
                await RememberSnapshotAsync(snapshot).ConfigureAwait(false);
                result = path is null
                    ? await SteamLaunchConfig.ApplyAsync(steam, appId, shortcut, mode, baseline, token).ConfigureAwait(false)
                    : await SteamLaunchConfig.ApplyCustomAsync(steam, appId, shortcut, path, arguments, token).ConfigureAwait(false);
                if (!result.Ok)
                {
                    if (existing is null)
                    {
                        await ForgetSnapshotAsync(appId).ConfigureAwait(false);
                    }
                    else
                    {
                        await RememberSnapshotAsync(existing).ConfigureAwait(false);
                    }
                }
            }

            _noticeAppId = appId;
            _notice = result.Ok && !result.Confirmed
                ? "Sent to Steam, but its answer was lost. The original is retained; refresh to check the current state."
                : result.Detail;

            Log.Info($"Launch action for {appId}: {(path is null ? mode.ToString() : "custom")}, "
                     + $"accepted={result.Ok}, confirmed={result.Confirmed}.");

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Could not change launch actions for {appId}", ex);
            return new LaunchConfigResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    internal static string Label(LaunchWrapperMode mode)
    {
        return mode switch
        {
            LaunchWrapperMode.Deelevate => "Run without admin",
            LaunchWrapperMode.InputLease or LaunchWrapperMode.InputLeaseInject => "Give the game the controller",
            LaunchWrapperMode.Both or LaunchWrapperMode.BothInject => "Run without admin + give the game the controller",
            _ => "No WSGM launch action"
        };
    }

    internal static LaunchWrapperConfig? CaptureSnapshot(long appId, bool shortcut, SteamAppDetails current,
        LaunchWrapperConfig? existing)
    {
        var original = LaunchWrapperCommand.OriginalsFrom(shortcut, current);
        if (existing is null && shortcut && LaunchWrapperCommand.TargetsHelper(current.ShortcutExe)
            && (string.IsNullOrWhiteSpace(original.Target) || LaunchWrapperCommand.TargetsHelper(original.Target)))
        {
            return null;
        }

        return new LaunchWrapperConfig
        {
            AppId = appId,
            IsShortcut = shortcut,
            OriginalTarget = existing?.OriginalTarget ?? original.Target,
            OriginalLaunchOptions = existing?.OriginalLaunchOptions ?? original.LaunchOptions,
            OriginalStartDir = existing?.OriginalStartDir ?? original.StartDir
        };
    }

    /// <summary>Finds the launch configuration a game had before WSGM changed it.</summary>
    private Task<LaunchWrapperConfig?> FindSnapshotAsync(long appId, CancellationToken cancellationToken)
    {
        return Task.Run(
            () => _store.Read().RequireConfig().LaunchWrappers.FirstOrDefault(w => w.AppId == appId),
            cancellationToken);
    }

    /// <summary>Records (or replaces) a game's original launch configuration so Remove can restore it.</summary>
    private Task RememberSnapshotAsync(LaunchWrapperConfig snapshot)
    {
        return Task.Run(() => _store.Update(config =>
        {
            config.LaunchWrappers.RemoveAll(w => w.AppId == snapshot.AppId);
            config.LaunchWrappers.Add(snapshot);

            return true;
        }));
    }

    /// <summary>Drops a game's snapshot once its launch configuration is restored.</summary>
    private Task ForgetSnapshotAsync(long appId)
    {
        return Task.Run(() => _store.Update(config =>
        {
            config.LaunchWrappers.RemoveAll(w => w.AppId == appId);
            return true;
        }));
    }

    private static bool MatchesCustom(bool shortcut, SteamAppDetails current, LaunchWrapperConfig snapshot)
    {
        if (!SteamCustomLaunchCommand.IsSupported(snapshot.CustomActionPath))
        {
            return false;
        }

        var fields = SteamCustomLaunchCommand.Build(snapshot.CustomActionPath, snapshot.CustomArguments);
        return shortcut
            ? current.ShortcutExe == fields.ShortcutTarget && current.ShortcutLaunchOptions == fields.ShortcutArguments
            : current.LaunchOptions == fields.LaunchOptions;
    }

    private static bool MatchesSnapshot(bool shortcut, SteamAppDetails current, LaunchWrapperConfig snapshot)
    {
        if (snapshot.Kind == LaunchConfigurationKind.CustomAction)
        {
            return MatchesCustom(shortcut, current, snapshot);
        }

        if (snapshot.Mode == LaunchWrapperMode.None)
        {
            return false;
        }

        var helper = LaunchWrapperCommand.HelperPathForCurrentDeployment();
        return shortcut
            ? current.ShortcutExe == LaunchWrapperCommand.ShortcutTarget(helper)
              && current.ShortcutLaunchOptions == LaunchWrapperCommand.ShortcutArguments(snapshot.Mode, snapshot.OriginalTarget, snapshot.OriginalLaunchOptions)
            : current.LaunchOptions == LaunchWrapperCommand.SteamLaunchOptions(helper, snapshot.Mode, snapshot.OriginalLaunchOptions);
    }
}
