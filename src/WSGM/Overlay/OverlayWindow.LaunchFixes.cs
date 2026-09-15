using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>The launch fix waiting on the user to pick a game, and the button
    /// whose title reports the outcome.</summary>
    private (LaunchWrapperMode Mode, CardButton Button)? _pendingLaunchFix;

    /// <summary>Re-labels the launch-fix rows for the current CEF state. Called when
    /// a config reload flips live configuration on or off under an open panel.</summary>
    internal void RefreshLaunchFixLabels()
    {
        if (DataContext is OverlayViewModel viewModel)
        {
            InitializeLaunchFixLabels(viewModel);
        }
    }

    // Set from the view model, because the same buttons do two different things:
    // with CEF on they configure the game in the running Steam client, with CEF off
    // they fall back to copying the command for the user to paste.
    private void InitializeLaunchFixLabels(OverlayViewModel viewModel)
    {
        var live = viewModel.ConfigureLaunchOptionsLive;
        DeelevateFixButton.Title = live ? "Fix: run without admin" : "Copy de-elevation command";
        DeelevateFixButton.Description = live
            ? "For games that refuse to start under elevated Steam"
            : "Paste into a game's Steam launch options";
        InputLeaseFixButton.Title = live ? "Fix: give the game the controller" : "Copy Steam Input block command";
        InputLeaseFixButton.Description = "For games that read the controller themselves";
        BothFixesButton.Title = live ? "Fix: both of the above" : "Copy combined command";
        BothFixesButton.Description = "No admin, and the game owns the controller";
        RemoveFixesButton.Title = "Restore original launch action";
        RemoveFixesButton.Description = "Remove WSGM changes and restore the original";
    }

    /// <summary>Starts the launch fix a row names in its CommandParameter. Pinned mirrors raise the
    /// click on their source row, so the sender is always the row that owns the result text.</summary>
    private void OnApplyLaunchFix(object? sender, RoutedEventArgs e)
    {
        if (sender is CardButton { CommandParameter: LaunchWrapperMode mode } row)
        {
            StartLaunchFix(mode, row);
        }
    }

    private async void OnPickCustomLaunchAction(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<IStorageFile> files;
            // The picker is a separate top-level window, so every touch in it lands
            // outside the bar. Suspend the controller's tap-outside dismissal (and
            // the gamepad driving the bar behind the dialog) until it closes.
            SystemDialogActive?.Invoke(true);
            try
            {
                files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Choose a custom launch action",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Launch actions")
                        {
                            Patterns = ["*.exe", "*.cmd", "*.bat", "*.ps1"],
                        },
                    ],
                });
            }
            finally
            {
                SystemDialogActive?.Invoke(false);
            }
            if (_closed || files.Count == 0 || !files[0].Path.IsFile)
            {
                return;
            }
            var path = files[0].Path.LocalPath;
            if (!SteamCustomLaunchCommand.IsSupported(path))
            {
                CustomLaunchButton.Title = "Unsupported file type";
                return;
            }
            LaunchWrapperHost.OpenCustom(path, await ResolveCurrentGameAsync(CustomLaunchButton));
            if (_closed)
            {
                return;
            }
            EnterSubView(OverlayPage.SteamLaunchConfiguration);
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                CustomLaunchButton.Title = "Couldn't choose a file";
            }
            Log.Error("Could not pick a custom launch action", ex);
        }
    }

    /// <summary>Resolves the game whose Steam page is on screen, so a custom action
    /// applies to it directly. Answers <c>null</c> for the library root and for a
    /// Steam that reported no current app — the caller then asks which game, exactly
    /// as <see cref="ApplyLaunchFixAsync"/> does for the wrapper buttons.</summary>
    private async Task<SteamCollections.AppInfo?> ResolveCurrentGameAsync(CardButton button)
    {
        button.Title = "Asking Steam…";
        var appId = await SteamPageBridge.GetCurrentAppIdAsync();
        if (_closed || appId <= 0)
        {
            return null;
        }
        var match = (await SafeGameLookupAsync()).FirstOrDefault(g => g.AppId == appId);
        // A game Steam knows about but the collection store did not list still
        // resolves: the id came from the page, and the shortcut flag from its range.
        return match ?? new SteamCollections.AppInfo(
            appId, appId.ToString(CultureInfo.InvariantCulture), appId >= 0x80000000L);
    }

    private void OnCustomLaunchGamePicked(
        string path, string arguments, SteamCollections.AppInfo game)
        => _ = ApplyCustomLaunchToAsync(path, arguments, game, CustomLaunchButton);

    private async System.Threading.Tasks.Task ApplyCustomLaunchToAsync(
        string path, string arguments, SteamCollections.AppInfo game, CardButton button)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                button.Title = "File is no longer available";
                return;
            }
            var details = await SteamLaunchConfig.ReadAsync(game.AppId);
            if (details is not { } current)
            {
                button.Title = "Steam didn't answer";
                return;
            }
            var existing = await LibraryTabManager.FindLaunchWrapperAsync(game.AppId);
            var originals = existing is null
                ? (current.ShortcutTarget,
                    game.Shortcut ? current.ShortcutArguments : current.LaunchOptions,
                    current.ShortcutStartDir)
                : (existing.OriginalTarget, existing.OriginalLaunchOptions, existing.OriginalStartDir);
            var snapshot = existing ?? new LaunchWrapperConfig
            {
                AppId = game.AppId,
                IsShortcut = game.Shortcut,
                OriginalTarget = originals.Item1,
                OriginalLaunchOptions = originals.Item2,
                OriginalStartDir = originals.Item3,
            };
            snapshot.Kind = LaunchConfigurationKind.CustomAction;
            snapshot.Mode = LaunchWrapperMode.None;
            snapshot.CustomActionPath = path;
            snapshot.CustomArguments = arguments;
            snapshot.Name = game.Name;
            if (existing is null)
            {
                // Persist the only restoration copy before Steam destroys a shortcut Target.
                await LibraryTabManager.RememberLaunchWrapperAsync(snapshot);
            }
            var result = await SteamLaunchConfig.ApplyCustomAsync(
                game.AppId, game.Shortcut, path, arguments);
            if (!result.Ok && existing is null)
            {
                await LibraryTabManager.ForgetLaunchWrapperAsync(game.AppId);
            }
            else if (result.Ok && existing is not null)
            {
                await LibraryTabManager.RememberLaunchWrapperAsync(snapshot);
            }
            button.Title = result.Ok ? $"Applied to {game.Name}" : result.Detail;
            if (result.Ok)
            {
                Log.Info($"Custom launch action written for {game.Name} ({game.AppId}).");
                LeaveSubView(OverlayPage.SteamLaunchConfiguration);
                await DismissAfterCopyFeedback();
            }
        }
        catch (Exception ex)
        {
            button.Title = "Couldn't configure launch action";
            Log.Error($"Could not configure custom launch action for {game.AppId}", ex);
        }
    }

    private void StartLaunchFix(LaunchWrapperMode mode, CardButton button)
    {
        // Resolve the lease route ONCE, here, before anything branches. The
        // clipboard text, the value written into Steam and the snapshot persisted
        // into config all flow from this, so deciding it in one place is what stops
        // them disagreeing about how a given game blocks Steam Input.
        mode = LaunchWrapperCommand.ForCurrentInputMode(
            mode, (DataContext as OverlayViewModel)?.InputLeaseUsesShim ?? true);
        var helperPath = LaunchWrapperCommand.HelperPathForCurrentDeployment();
        if (mode != LaunchWrapperMode.None && !System.IO.File.Exists(helperPath))
        {
            button.Title = "Launch wrapper missing";
            Log.Warn($"Cannot configure a launch fix; wrapper not found: {helperPath}");
            return;
        }

        if (DataContext is not OverlayViewModel { ConfigureLaunchOptionsLive: true })
        {
            _ = CopyLaunchCommandAsync(mode, button, helperPath);
            return;
        }
        _ = ApplyLaunchFixAsync(mode, button);
    }

    private async System.Threading.Tasks.Task CopyLaunchCommandAsync(
        LaunchWrapperMode mode, CardButton button, string helperPath)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            button.Title = "Clipboard unavailable";
            Log.Warn("Cannot copy a launch command; no clipboard is available.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(LaunchWrapperCommand.SteamLaunchOptions(helperPath, mode));
            button.Title = "Copied to clipboard";
            Log.Info($"Copied the {mode} launch-option command to clipboard.");
            await DismissAfterCopyFeedback();
        }
        catch (Exception ex)
        {
            button.Title = "Clipboard copy failed";
            Log.Error("Could not copy the launch command", ex);
        }
    }

    // A copied command means the user is heading to Steam to paste it: show the
    // "Copied" confirmation briefly, then dismiss the panel (which restores Steam
    // to the foreground). Same rule as the actions that open a window.
    private static async System.Threading.Tasks.Task FeedbackDelay()
        => await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(700));

    private async System.Threading.Tasks.Task DismissAfterCopyFeedback()
    {
        await FeedbackDelay();
        if (_closed)
        {
            // The panel was dismissed and re-opened while the confirmation showed:
            // the controller wires Dismissed per window instance, so this stale
            // window would close the live panel out from under the user.
            Log.Info("Launch-fix feedback dismissal skipped — its panel is already closed.");
            return;
        }
        Dismissed?.Invoke();
    }

    private async System.Threading.Tasks.Task ApplyLaunchFixAsync(
        LaunchWrapperMode mode, CardButton button)
    {
        button.Title = "Asking Steam…";
        var appId = await SteamPageBridge.GetCurrentAppIdAsync();
        if (appId <= 0)
        {
            // Nothing on screen identifies a game (the library root, or a Steam that
            // did not answer): ask which one instead of guessing.
            _pendingLaunchFix = (mode, button);
            LaunchWrapperHost.Open(mode == LaunchWrapperMode.None
                ? "Remove launch fixes"
                : "Apply launch fix");
            EnterSubView(OverlayPage.SteamLaunchConfiguration);
            return;
        }

        var games = await SafeGameLookupAsync();
        var match = games.FirstOrDefault(g => g.AppId == appId);
        await ApplyLaunchFixToAsync(
            mode, button, appId, match?.Name ?? appId.ToString(CultureInfo.InvariantCulture),
            match?.Shortcut ?? appId >= 0x80000000L);
    }

    private async System.Threading.Tasks.Task ApplyLaunchFixToAsync(
        LaunchWrapperMode mode, CardButton button, long appId, string name, bool isShortcut)
    {
        try
        {
            var current = await SteamLaunchConfig.ReadAsync(appId);
            if (current is not { } details)
            {
                button.Title = "Steam didn't answer";
                return;
            }

            LaunchConfigResult result;
            if (mode == LaunchWrapperMode.None)
            {
                var snapshot = await LibraryTabManager.FindLaunchWrapperAsync(appId);
                if (snapshot is null)
                {
                    button.Title = $"No fix applied to {name}";
                    return;
                }
                result = await SteamLaunchConfig.RestoreAsync(snapshot);
                if (result.Ok)
                {
                    await LibraryTabManager.ForgetLaunchWrapperAsync(appId);
                }
            }
            else
            {
                var existing = await LibraryTabManager.FindLaunchWrapperAsync(appId);
                // Snapshot BEFORE the write: configuring a shortcut overwrites its
                // Target, so this becomes the only record of the real program. When
                // the game is already wrapped (the user is switching modes) the
                // values on screen are WSGM's own — keep the first snapshot instead,
                // and when there is none (the command was pasted by hand, or the
                // config was reset) unwrap them rather than recording the wrapper as
                // the "original", which would make Remove restore the wrapper itself.
                var originals = SteamLaunchConfig.OriginalsFrom(isShortcut, details);
                var wrapped = SteamLaunchConfig.ModeFor(isShortcut, details) != LaunchWrapperMode.None;
                if (wrapped && existing is null && isShortcut
                    && string.IsNullOrWhiteSpace(originals.Target))
                {
                    // A wrapped shortcut whose real program cannot be recovered has
                    // no restorable state; writing WSGM's own values as the original
                    // would strand it permanently.
                    button.Title = "Can't read the original program";
                    Log.Warn($"Launch fix refused for {name} ({appId}): the shortcut is already "
                        + "wrapped and its original target could not be recovered.");
                    return;
                }
                var snapshot = existing ?? new LaunchWrapperConfig
                {
                    AppId = appId,
                    IsShortcut = isShortcut,
                    OriginalTarget = originals.Target,
                    OriginalLaunchOptions = originals.LaunchOptions,
                    OriginalStartDir = originals.StartDir,
                };
                snapshot.Kind = LaunchConfigurationKind.Wrapper;
                snapshot.Mode = mode;
                snapshot.CustomActionPath = "";
                snapshot.CustomArguments = "";
                snapshot.Name = name;
                await LibraryTabManager.RememberLaunchWrapperAsync(snapshot);

                result = await SteamLaunchConfig.ApplyAsync(appId, isShortcut, mode, details);
                if (!result.Ok && existing is null)
                {
                    // Nothing was changed in Steam, so leave no snapshot behind
                    // claiming otherwise — unless one was already there.
                    await LibraryTabManager.ForgetLaunchWrapperAsync(appId);
                }
            }

            button.Title = result.Ok
                ? mode == LaunchWrapperMode.None ? $"Removed from {name}" : $"Applied to {name}"
                : result.Detail;
            if (result.Ok)
            {
                Log.Info($"Launch fix {mode} written for {name} ({appId}).");
                await DismissAfterCopyFeedback();
            }
        }
        catch (Exception ex)
        {
            button.Title = "Couldn't reach Steam";
            Log.Error($"Could not configure the launch fix for {appId}", ex);
        }
    }

    private static async System.Threading.Tasks.Task<IReadOnlyList<SteamCollections.AppInfo>>
        SafeGameLookupAsync()
    {
        try { return await SteamCollections.GetGamesAsync(); }
        catch (Exception ex)
        {
            Log.Warn($"Could not list games while configuring a launch fix: {ex.Message}");
            return [];
        }
    }

    private void OnLaunchFixGamePicked(SteamCollections.AppInfo game)
    {
        if (_pendingLaunchFix is not { } pending)
        {
            LeaveSubView(OverlayPage.SteamLaunchConfiguration);
            return;
        }
        LeaveSubView(OverlayPage.SteamLaunchConfiguration);
        _ = ApplyLaunchFixToAsync(pending.Mode, pending.Button, game.AppId, game.Name, game.Shortcut);
    }
}
