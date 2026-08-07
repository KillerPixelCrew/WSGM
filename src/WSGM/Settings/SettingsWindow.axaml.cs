using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Settings;

/// <summary>The interactive settings window for shell and game-mode configuration.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel = new();
    private readonly GamepadService _gamepad = new();
    private GamepadNavigation? _navigation;
    private OverlayController? _testOverlay;
    private KeyRecorder? _keyRecorder;
    private GamepadChordRecorder? _chordRecorder;
    private bool _closed;

    /// <summary>Creates the settings window and connects its input recorders.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Controller navigation for the settings window itself.
        Opened += (_, _) =>
        {
            _navigation = new GamepadNavigation(_gamepad, this, back: Close,
                isNintendoLayout: () => _viewModel.GlyphStyleIndex == 2);
            _gamepad.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _gamepad.Stop();
            _navigation?.Dispose();
            _navigation = null;
            _testOverlay?.Dispose();
            _testOverlay = null;
            _keyRecorder?.Dispose();
            _keyRecorder = null;
            _chordRecorder?.Dispose();
            _chordRecorder = null;
        };
    }

    /// <summary>NavigationView is the Avalonia equivalent of Handheld Companion's
    /// AdaptiveNavigationView. Keep page selection in one place so touch, mouse,
    /// keyboard, and controller navigation all land on the same settings page.</summary>
    private void OnNavigationItemInvoked(object? sender, NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer?.Tag is not string tag ||
            !int.TryParse(tag, out var page) || page is < 0 or > 3)
        {
            return;
        }

        SystemPage.IsVisible = page == 0;
        HomePage.IsVisible = page == 1;
        StartupPage.IsVisible = page == 2;
        QuickAccessPage.IsVisible = page == 3;
    }

    private void OnUninstall(object? sender, RoutedEventArgs e) => _viewModel.Uninstall();

    private void OnInstallApp(object? sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.InstallApp();
        }
        catch (Exception ex)
        {
            Log.Error("App install failed", ex);
            SaveStatus.Text = $"Install failed: {ex.Message}";
        }
    }

    private async void OnRecordHotkey(object? sender, RoutedEventArgs e)
    {
        // Small delay so the key/controller press that started recording (Enter, A)
        // isn't the thing we record — same trick Handheld Companion uses.
        _viewModel.SetHotkeyRecording(true);
        await System.Threading.Tasks.Task.Delay(200);
        if (_closed)
        {
            // Window closed during the delay: creating the recorder now would
            // install a low-level keyboard hook with nothing left to dispose it.
            return;
        }

        _keyRecorder?.Dispose();
        _keyRecorder = new KeyRecorder();
        _keyRecorder.Recorded += (modifiers, vk) =>
        {
            _viewModel.ApplyRecordedHotkey(modifiers, vk);
            _keyRecorder?.Dispose();
            _keyRecorder = null;
        };
        _keyRecorder.Start();
    }

    private void OnClearHotkey(object? sender, RoutedEventArgs e)
    {
        _keyRecorder?.Dispose();
        _keyRecorder = null;
        _viewModel.ClearHotkey();
    }

    private async void OnRecordChord(object? sender, RoutedEventArgs e)
    {
        _viewModel.SetChordRecording(true);
        await System.Threading.Tasks.Task.Delay(200);
        if (_closed)
        {
            // Same race as OnRecordHotkey: no recorder after the window is gone.
            return;
        }

        _chordRecorder?.Dispose();
        _chordRecorder = new GamepadChordRecorder();
        _chordRecorder.Recorded += (buttons, hold) =>
        {
            _viewModel.ApplyRecordedChord(buttons, hold);
            _chordRecorder?.Dispose();
            _chordRecorder = null;
        };
        _chordRecorder.Start();
    }

    private void OnClearChord(object? sender, RoutedEventArgs e)
    {
        _chordRecorder?.Dispose();
        _chordRecorder = null;
        _viewModel.ClearChord();
    }

    private void OnToggleLockOnWake(object? sender, RoutedEventArgs e)
    {
        var wanted = LockOnWakeCheckBox.IsChecked == true;
        _viewModel.SetLockOnWake(wanted);
        LockOnWakeCheckBox.IsChecked = _viewModel.LockOnWakeDisabled;
    }

    private void OnToggleUac(object? sender, RoutedEventArgs e)
    {
        // The checkbox mirrors machine state, not a config value: ask Windows to
        // change it (one elevation prompt), then re-read whatever actually stuck.
        var wanted = UacCheckBox.IsChecked == true;
        _viewModel.SetUacPrompts(wanted);
        UacCheckBox.IsChecked = _viewModel.UacPromptsDisabled;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.Save();
            SaveStatus.Text = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            // A failed config write (locked/read-only file) must not escape a
            // click handler — in-shell it would hit the panic path.
            Log.Error("Saving settings failed", ex);
            SaveStatus.Text = $"Save failed: {ex.Message}";
        }
    }

    private async void OnAddApp(object? sender, RoutedEventArgs e)
    {
        // A detected suggestion adds itself; "Choose a program…" opens the picker.
        if (_viewModel.AddSelectedStartupApp())
        {
            return;
        }
        var path = await PickExeAsync();
        if (path is not null)
        {
            _viewModel.StartupApps.Add(new StartupAppRow { Path = path, Enabled = true });
        }
    }

    private void OnRemoveApp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupAppRow row)
        {
            _viewModel.RemoveStartupApp(row);
        }
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupAppRow row)
        {
            _viewModel.MoveStartupApp(row, -1);
        }
    }

    private void OnMoveDown(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupAppRow row)
        {
            _viewModel.MoveStartupApp(row, +1);
        }
    }

    private async void OnBrowseStartupApp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupAppRow row)
        {
            var path = await PickExeAsync();
            if (path is not null)
            {
                row.Path = path;
            }
        }
    }

    private async System.Threading.Tasks.Task<string?> PickExeAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select application",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Applications") { Patterns = ["*.exe"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnTestOverlay(object? sender, RoutedEventArgs e)
    {
        // Use the real controller so behavior matches shell mode exactly. Rebuild it
        // for every test so unsaved glyph/input changes take effect immediately.
        _testOverlay?.Dispose();
        var config = _viewModel.SnapshotForTest();
        _testOverlay = new OverlayController(config, monitor: null, new SessionModes(config, monitor: null));
        _testOverlay.ShowOverlay();
    }

    private void OnTestTaskbar(object? sender, RoutedEventArgs e)
    {
        // Direct ShowTaskbar: the swipe routing's game-mode gate would bounce a
        // dev desktop (explorer alive) back to quick access, so the button
        // bypasses routing to make the bar locally testable.
        _testOverlay?.Dispose();
        var config = _viewModel.SnapshotForTest();
        _testOverlay = new OverlayController(config, monitor: null, new SessionModes(config, monitor: null));
        _testOverlay.ShowTaskbar();
    }

    private void OnTouchKeyboard(object? sender, RoutedEventArgs e)
    {
        // Custom-shell sessions have no taskbar to summon the touch keyboard from.
        // TabTip only — the osk.exe fallback brought up the legacy accessibility
        // keyboard, which is never the right thing on a touch handheld.
        var tabTip = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            @"microsoft shared\ink\TabTip.exe");
        if (File.Exists(tabTip))
        {
            AppLauncher.Open(tabTip);
        }
        else
        {
            Log.Warn($"Touch keyboard host not found: {tabTip}");
        }
    }
}
