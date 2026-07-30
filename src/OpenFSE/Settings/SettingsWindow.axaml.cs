using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenFSE.Core;
using OpenFSE.Input;
using OpenFSE.Overlay;

namespace OpenFSE.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel = new();
    private readonly GamepadService _gamepad = new();
    private GamepadNavigation? _navigation;
    private OverlayController? _testOverlay;

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
            _gamepad.Stop();
            _navigation?.Dispose();
            _navigation = null;
            _testOverlay?.Dispose();
            _testOverlay = null;
        };
    }

    private void OnInstall(object? sender, RoutedEventArgs e)
    {
        _viewModel.Save();
        _viewModel.Install();
    }

    private void OnUninstall(object? sender, RoutedEventArgs e) => _viewModel.Uninstall();

    private void OnInstallApp(object? sender, RoutedEventArgs e) => _viewModel.InstallApp();

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
        _viewModel.Save();
        SaveStatus.Text = $"Saved {DateTime.Now:HH:mm:ss}";
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

    private async void OnBrowseHomeApp(object? sender, RoutedEventArgs e)
    {
        var path = await PickExeAsync();
        if (path is not null)
        {
            _viewModel.HomeAppPath = path;
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
        _testOverlay = new OverlayController(_viewModel.SnapshotForTest(), monitor: null);
        _testOverlay.ShowOverlay();
    }

    private void OnTouchKeyboard(object? sender, RoutedEventArgs e)
    {
        // Custom-shell sessions have no taskbar to summon the touch keyboard from.
        var tabTip = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            @"microsoft shared\ink\TabTip.exe");
        try
        {
            if (File.Exists(tabTip))
            {
                Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to open touch keyboard", ex);
        }
    }
}
