using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Core;

namespace WSGM.Settings.Pages;

/// <summary>Ownership-only Device Integration settings and read-only coordinator status.</summary>
public partial class DeviceOwnershipPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public DeviceOwnershipPage()
    {
        InitializeComponent();
    }

    /// <summary>Returns focus to the row that opened the profile editor.</summary>
    internal void FocusOpenProfiles()
    {
        OpenProfiles.Focus(NavigationMethod.Directional);
    }

    /// <summary>Raised when the profile editor is asked for; the window owns its navigation.</summary>
    internal event Action? OpenProfilesRequested;

    private void OnOpenProfiles(object? sender, RoutedEventArgs e)
    {
        OpenProfilesRequested?.Invoke();
    }

    private void OnRefreshStatus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            Log.Observe(viewModel.RefreshDeviceOwnerStatusAsync(), "device owner status refresh");
        }
    }
}
