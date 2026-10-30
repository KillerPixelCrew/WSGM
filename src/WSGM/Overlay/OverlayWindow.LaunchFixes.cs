using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions.

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>
    ///     The launch fix waiting on the user to pick a game, and the button
    ///     whose title reports the outcome.
    /// </summary>
    private (LaunchWrapperMode Mode, ActionButton Button)? _pendingLaunchFix;
    private LaunchActionService? _launchActions;

    /// <summary>Attaches the single session launch-action owner used by Steam and Overlay.</summary>
    internal void AttachLaunchActions(LaunchActionService? service)
    {
        _launchActions = service;
        LaunchWrapperHost.AttachLaunchActions(service);
    }

    /// <summary>
    ///     Re-labels the launch-fix rows for the current CEF state. Called when
    ///     a config reload flips live configuration on or off under an open panel.
    /// </summary>
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

    /// <summary>
    ///     Starts the launch fix a row names in its CommandParameter. Pinned mirrors raise the
    ///     click on their source row, so the sender is always the row that owns the result text.
    /// </summary>
    private void OnApplyLaunchFix(object? sender, RoutedEventArgs e)
    {
        if (sender is ActionButton { CommandParameter: LaunchWrapperMode mode } row)
        {
            StartLaunchFix(mode, row);
        }
    }

    private async void OnPickCustomLaunchAction(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Keep the game identity from before opening the file picker.
            var game = await ResolveCurrentGameAsync(CustomLaunchButton);
            var path = await PickLocalPathAsync(false, ".exe", ".cmd", ".bat", ".ps1");
            if (_closed || path is null)
            {
                return;
            }

            if (!SteamCustomLaunchCommand.IsSupported(path))
            {
                CustomLaunchButton.Title = "Unsupported file type";
                return;
            }

            LaunchWrapperHost.OpenCustom(path, game);
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

    /// <summary>
    ///     Resolves the game whose Steam page is on screen, so a custom action
    ///     applies to it directly. Answers <c>null</c> for the library root and for a
    ///     Steam that reported no current app — the caller then asks which game, exactly
    ///     as <see cref="ApplyLaunchFixAsync" /> does for the wrapper buttons.
    /// </summary>
    private async Task<SteamLibraryApp?> ResolveCurrentGameAsync(ActionButton button)
    {
        var title = button.Title;
        button.Title = "Asking Steam…";
        var appId = await SteamCurrentGame.AppIdAsync(_steam);
        if (_closed)
        {
            return null;
        }

        // The caller opens the launch-action page next, so the row stops claiming it is waiting
        // on Steam whether or not a game was found.
        var game = appId == 0 ? null : await LookupGameAsync(appId);
        button.Title = title;
        return game;
    }

    /// <summary>
    ///     The listed game for a page's app id. A game Steam knows about but the collection store
    ///     did not list, or a lookup that failed, still resolves: the id came from the page, and
    ///     the shortcut flag from its range.
    /// </summary>
    private async Task<SteamLibraryApp> LookupGameAsync(uint appId)
    {
        var result = await OverlayLibraryLookup.ReadAsync(_steam);
        if (!result.Succeeded)
        {
            Log.Warn($"Could not list Steam's games for {appId}; using the page's id: {result.Error}");
        }

        return result.Games.FirstOrDefault(g => g.AppId == appId)
               ?? new SteamLibraryApp(
                   appId, appId.ToString(CultureInfo.InvariantCulture), SteamApps.IsShortcutAppId(appId));
    }

    private void OnCustomLaunchGamePicked(
        string path, string arguments, SteamLibraryApp game)
    {
        _ = ApplyCustomLaunchToAsync(path, arguments, game, CustomLaunchButton);
    }

    private Task ApplyCustomLaunchToAsync(
        string path, string arguments, SteamLibraryApp game, ActionButton button)
    {
        if (_launchActions is null)
        {
            button.Title = "Launch actions unavailable";
            return Task.CompletedTask;
        }

        LaunchWrapperHost.OpenActions(game, LaunchWrapperMode.None, path, arguments);
        return Task.CompletedTask;
    }

    private void StartLaunchFix(LaunchWrapperMode mode, ActionButton button)
    {
        if (DataContext is not OverlayViewModel { ConfigureLaunchOptionsLive: true })
        {
            if (mode == LaunchWrapperMode.None)
            {
                button.Title = "Enable Steam UI integration to restore launch actions";
                return;
            }

            _ = CopyLaunchCommandAsync(mode, button);
            return;
        }

        _ = ApplyLaunchFixAsync(mode, button);
    }

    private async Task CopyLaunchCommandAsync(
        LaunchWrapperMode mode, ActionButton button)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            button.Title = "Clipboard unavailable";
            Log.Warn("Cannot copy a launch command; no clipboard is available.");
            return;
        }

        try
        {
            if (_launchActions is null)
            {
                button.Title = "Launch actions unavailable";
                return;
            }

            await clipboard.SetTextAsync(_launchActions.ClipboardCommand(mode));
            button.Title = "Copied to clipboard";
            Log.Info($"Copied the {mode} launch-option command to clipboard.");
            await DismissAfterCopyFeedback();
        }
        catch (Exception ex)
        {
            button.Title = ex.Message;
            Log.Error("Could not copy the launch command", ex);
        }
    }

    // A copied command means the user is heading to Steam to paste it: show the
    // "Copied" confirmation briefly, then dismiss the panel (which restores Steam
    // to the foreground). Same rule as the actions that open a window.
    private static async Task FeedbackDelay()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(700));
    }

    private async Task DismissAfterCopyFeedback()
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

    private async Task ApplyLaunchFixAsync(
        LaunchWrapperMode mode, ActionButton button)
    {
        var title = button.Title;
        try
        {
            button.Title = "Asking Steam…";
            var appId = await SteamCurrentGame.AppIdAsync(_steam);
            if (_closed)
            {
                return;
            }

            if (appId == 0)
            {
                // Nothing on screen identifies a game (the library root, or a Steam that
                // did not answer): ask which one instead of guessing.
                button.Title = title;
                _pendingLaunchFix = (mode, button);
                LaunchWrapperHost.Open(mode == LaunchWrapperMode.None
                    ? "Remove launch fixes"
                    : "Apply launch fix");
                EnterSubView(OverlayPage.SteamLaunchConfiguration);
                return;
            }

            var game = await LookupGameAsync(appId);
            await ApplyLaunchFixToAsync(mode, button, game.AppId, game.Name, game.Shortcut);
        }
        catch (Exception ex)
        {
            button.Title = "Couldn't reach Steam";
            Log.Error("Could not resolve the game for a launch fix", ex);
        }
    }

    private Task ApplyLaunchFixToAsync(
        LaunchWrapperMode mode, ActionButton button, long appId, string name, bool isShortcut)
    {
        if (_launchActions is null)
        {
            button.Title = "Launch actions unavailable";
            return Task.CompletedTask;
        }

        LaunchWrapperHost.OpenActions(new SteamLibraryApp(SteamApps.NormalizeAppId(appId), name, isShortcut), mode);
        EnterSubView(OverlayPage.SteamLaunchConfiguration);
        return Task.CompletedTask;
    }

    /// <summary>Keeps the first restoration record, unwrapping a manually configured launch fix when needed.</summary>
    /// <param name="appId">Steam application or shortcut identity.</param>
    /// <param name="isShortcut">Whether the target is a non-Steam shortcut.</param>
    /// <param name="details">Current Steam launch fields read for this target.</param>
    /// <param name="existing">Previously owned launch snapshot, if any.</param>
    /// <returns>A launch snapshot when a safe unwrapped baseline is available, otherwise null.</returns>
    internal static LaunchWrapperConfig? CaptureLaunchSnapshot(long appId, bool isShortcut, SteamAppDetails details,
        LaunchWrapperConfig? existing)
    {
        return existing ?? LaunchActionService.CaptureSnapshot(appId, isShortcut, details, null);
    }

    private void OnLaunchFixGamePicked(SteamLibraryApp game)
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
