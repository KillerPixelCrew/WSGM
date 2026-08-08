using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Settings.Pages;

/// <summary>The Steam settings page: Big Picture status, auto-relaunch, and the
/// two machine-policy toggles (UAC prompts, lock on wake). Inherits the window's
/// <see cref="SettingsViewModel"/> DataContext.</summary>
public partial class SteamPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public SteamPage() => InitializeComponent();

    private void OnToggleUac(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }
        // The toggle mirrors machine state, not a config value: ask Windows to
        // change it (one elevation prompt), then re-read whatever actually stuck.
        var wanted = UacCheckBox.IsChecked == true;
        viewModel.SetUacPrompts(wanted);
        UacCheckBox.IsChecked = viewModel.UacPromptsDisabled;
    }

    private void OnToggleLockOnWake(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }
        var wanted = LockOnWakeCheckBox.IsChecked == true;
        viewModel.SetLockOnWake(wanted);
        LockOnWakeCheckBox.IsChecked = viewModel.LockOnWakeDisabled;
    }
}
