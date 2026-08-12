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

    // The window hosts the keyboard dialog: it owns the gamepad service and the
    // navigation swap the dialog needs, and without that swap the keys are
    // unreachable by pad while the settings page behind the modal still answers
    // presses (a machine-policy toggle sits on this very page).
    private void OnOpenApiKeyKeyboard(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is SettingsWindow window)
        {
            window.ShowOnScreenKeyboard(SteamGridDbKeyBox, "SteamGridDB API key");
        }
    }

    // async void is the framework event-handler form; the awaited work is a Task on
    // the view model and its continuation resumes on the UI thread. The toggle is
    // disabled meanwhile so a second press cannot queue a second elevation prompt.
    private async void OnToggleUac(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }
        // The toggle mirrors machine state, not a config value: ask Windows to
        // change it (one elevation prompt), then re-read whatever actually stuck.
        var wanted = UacCheckBox.IsChecked == true;
        UacCheckBox.IsEnabled = false;
        try
        {
            await viewModel.SetUacPromptsAsync(wanted);
        }
        finally
        {
            UacCheckBox.IsEnabled = true;
            UacCheckBox.IsChecked = viewModel.UacPromptsDisabled;
        }
    }

    private async void OnToggleLockOnWake(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }
        var wanted = LockOnWakeCheckBox.IsChecked == true;
        LockOnWakeCheckBox.IsEnabled = false;
        try
        {
            await viewModel.SetLockOnWakeAsync(wanted);
        }
        finally
        {
            LockOnWakeCheckBox.IsEnabled = true;
            LockOnWakeCheckBox.IsChecked = viewModel.LockOnWakeDisabled;
        }
    }
}
