using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions.

namespace WSGM.Overlay;

/// <summary>
///     Steam > Per-game launch fixes: de-elevation, the Steam Input lease, both, a custom launch action and
///     the restore, applied to the game on Steam's page or to one the user picks.
/// </summary>
/// <remarks>
///     With Steam UI integration on, a fix configures the game in the running Steam client through the
///     launch configuration page; with it off, the same rows copy the command for the user to paste. Each
///     row reports its outcome in its own title, which pinned copies follow.
/// </remarks>
public sealed partial class LaunchFixesPage : StackPanel
{
    private bool _closed;
    private LaunchActionService? _launchActions;
    private LaunchWrapperView? _launchWrapper;

    /// <summary>
    ///     The launch fix waiting on the user to pick a game, and the button
    ///     whose title reports the outcome.
    /// </summary>
    private (LaunchWrapperMode Mode, ActionButton Button)? _pendingLaunchFix;

    private OverlaySurfaceStack? _surfaces;

    /// <summary>Builds the rows; the window supplies the launch configuration page with <see cref="Configure" />.</summary>
    public LaunchFixesPage()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     The session's Steam client, or null on a surface without one, whose rows then report Steam as
    ///     unreachable.
    /// </summary>
    internal SteamClient? Steam { get; set; }

    /// <summary>Raised to open the launch configuration page.</summary>
    internal event Action? ConfigurationRequested;

    /// <summary>Raised to close the launch configuration page.</summary>
    internal event Action? ConfigurationClosed;

    /// <summary>Raised when a copied command sends the user to Steam, which closes the sheet.</summary>
    internal event Action? DismissRequested;

    /// <summary>Connects the page to the launch configuration page and the sheet's file picker.</summary>
    /// <param name="launchWrapper">The launch configuration page.</param>
    /// <param name="surfaces">The sheet's surfaces, which host the file picker.</param>
    internal void Configure(LaunchWrapperView launchWrapper, OverlaySurfaceStack surfaces)
    {
        _launchWrapper = launchWrapper;
        _surfaces = surfaces;
        launchWrapper.Picked += OnLaunchFixGamePicked;
        launchWrapper.CustomPicked += OnCustomLaunchGamePicked;
    }

    /// <summary>Attaches the single session launch-action owner used by Steam and Overlay.</summary>
    /// <param name="service">The owner, or null without one.</param>
    internal void AttachLaunchActions(LaunchActionService? service)
    {
        _launchActions = service;
        _launchWrapper?.AttachLaunchActions(service);
    }

    /// <summary>Drops the results of work still running as the window closes.</summary>
    internal void Detach()
    {
        _closed = true;
    }

    /// <summary>
    ///     Re-labels the launch-fix rows for the current CEF state. Called as the sheet is built and when
    ///     a config reload flips live configuration on or off under an open panel.
    /// </summary>
    internal void RefreshLabels()
    {
        // Set from the view model, because the same buttons do two different things:
        // with CEF on they configure the game in the running Steam client, with CEF off
        // they fall back to copying the command for the user to paste.
        if (DataContext is not OverlayViewModel viewModel)
        {
            return;
        }

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

    /// <summary>Forgets the fix waiting on a game pick as its configuration page closes.</summary>
    internal void ForgetPendingFix()
    {
        _pendingLaunchFix = null;
        // Clears the "Asking Steam…" title left on whichever button opened the picker.
        // A pick re-writes it moments later with the real outcome.
        RefreshLabels();
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
            var path = _surfaces is null
                ? null
                : await _surfaces.PickLocalPathAsync(false, ".exe", ".cmd", ".bat", ".ps1");
            if (_closed || path is null)
            {
                return;
            }

            if (!SteamCustomLaunchCommand.IsSupported(path))
            {
                CustomLaunchButton.Title = "Unsupported file type";
                return;
            }

            _launchWrapper?.OpenCustom(path, game);
            if (_closed)
            {
                return;
            }

            ConfigurationRequested?.Invoke();
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
        var appId = await SteamCurrentGame.AppIdAsync(Steam);
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
        var result = await OverlayLibraryLookup.ReadAsync(Steam);
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
        if (_launchActions is null)
        {
            CustomLaunchButton.Title = "Launch actions unavailable";
            return;
        }

        _launchWrapper?.OpenActions(game, LaunchWrapperMode.None, path, arguments);
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
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
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
    private async Task DismissAfterCopyFeedback()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        if (_closed)
        {
            // The panel was dismissed and re-opened while the confirmation showed:
            // the controller wires Dismissed per window instance, so this stale
            // window would close the live panel out from under the user.
            Log.Info("Launch-fix feedback dismissal skipped — its panel is already closed.");
            return;
        }

        DismissRequested?.Invoke();
    }

    private async Task ApplyLaunchFixAsync(
        LaunchWrapperMode mode, ActionButton button)
    {
        var title = button.Title;
        try
        {
            button.Title = "Asking Steam…";
            var appId = await SteamCurrentGame.AppIdAsync(Steam);
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
                _launchWrapper?.Open(mode == LaunchWrapperMode.None
                    ? "Remove launch fixes"
                    : "Apply launch fix");
                ConfigurationRequested?.Invoke();
                return;
            }

            var game = await LookupGameAsync(appId);
            ApplyLaunchFixTo(mode, button, game.AppId, game.Name, game.Shortcut);
        }
        catch (Exception ex)
        {
            button.Title = "Couldn't reach Steam";
            Log.Error("Could not resolve the game for a launch fix", ex);
        }
    }

    private void ApplyLaunchFixTo(
        LaunchWrapperMode mode, ActionButton button, long appId, string name, bool isShortcut)
    {
        if (_launchActions is null)
        {
            button.Title = "Launch actions unavailable";
            return;
        }

        _launchWrapper?.OpenActions(new SteamLibraryApp(SteamApps.NormalizeAppId(appId), name, isShortcut), mode);
        ConfigurationRequested?.Invoke();
    }

    private void OnLaunchFixGamePicked(SteamLibraryApp game)
    {
        var pending = _pendingLaunchFix;
        ConfigurationClosed?.Invoke();
        if (pending is { } fix)
        {
            ApplyLaunchFixTo(fix.Mode, fix.Button, game.AppId, game.Name, game.Shortcut);
        }
    }
}
