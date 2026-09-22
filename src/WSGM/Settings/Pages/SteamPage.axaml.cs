using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using WSGM.Core;

namespace WSGM.Settings.Pages;

/// <summary>
///     The Steam settings page: Big Picture status, auto-relaunch, and the
///     two machine-policy toggles (UAC prompts, lock on wake). Inherits the window's
///     <see cref="SettingsViewModel" /> DataContext.
/// </summary>
public partial class SteamPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public SteamPage()
    {
        InitializeComponent();
    }

    /// <summary>Opens the controller keyboard for one of the otherwise skipped text fields.</summary>
    /// <remarks>Gamepad navigation skips text boxes, so each one needs its own explicit way in.</remarks>
    private void OnEditTextWithController(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not SettingsWindow window)
        {
            return;
        }

        switch ((sender as Button)?.Tag as string)
        {
            case "SteamGridDbKey":
                window.ShowOnScreenKeyboard(SteamGridDbKeyBox, "SteamGridDB key");
                break;
            case "ScreenscraperUser":
                window.ShowOnScreenKeyboard(ScreenscraperUserBox, "Screenscraper account");
                break;
            case "ScreenscraperPassword":
                window.ShowOnScreenKeyboard(ScreenscraperPasswordBox, "Screenscraper password");
                break;
        }
    }

    private void OnToggleUac(object? sender, RoutedEventArgs e)
    {
        ObservePolicyChange(() => TogglePolicyAsync(
            UacCheckBox,
            static (viewModel, wanted) => viewModel.SetUacPromptsAsync(wanted),
            static viewModel => viewModel.UacPromptsDisabled), "UAC policy change");
    }

    private void OnToggleLockOnWake(object? sender, RoutedEventArgs e)
    {
        ObservePolicyChange(() => TogglePolicyAsync(
            LockOnWakeCheckBox,
            static (viewModel, wanted) => viewModel.SetLockOnWakeAsync(wanted),
            static viewModel => viewModel.LockOnWakeDisabled), "wake sign-in policy change");
    }

    private void ObservePolicyChange(Func<Task> action, string operation)
    {
        _ = ObservePolicyChangeAsync(action, operation);
    }

    private async Task ObservePolicyChangeAsync(
        Func<Task> action,
        string operation)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"{operation} failed: {ex.Message}");
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.StatusText = $"{operation} failed: {ex.Message}";
            }
        }
    }

    /// <summary>
    ///     Runs one machine-policy change behind its toggle. The toggle mirrors
    ///     machine state, not a config value: ask Windows to change it (one elevation
    ///     prompt), then re-read whatever actually stuck. The box is disabled meanwhile
    ///     so a second press cannot queue a second elevation prompt.
    /// </summary>
    private async Task TogglePolicyAsync(
        ToggleButton box,
        Func<SettingsViewModel, bool, Task<bool>> change,
        Func<SettingsViewModel, bool> current)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        var wanted = box.IsChecked == true;
        box.IsEnabled = false;
        try
        {
            await change(viewModel, wanted);
        }
        finally
        {
            box.IsEnabled = true;
            try
            {
                box.IsChecked = current(viewModel);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Machine-policy readback failed: {ex.Message}");
                viewModel.StatusText = $"Could not read the resulting Windows policy: {ex.Message}";
            }
        }
    }
}
