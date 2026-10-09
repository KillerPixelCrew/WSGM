using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Settings.Pages;

/// <summary>Authors saved fan curves and lighting colours without opening hardware.</summary>
public partial class DeviceProfilesPage : UserControl
{
    // #AARRGGBB, the longest colour text the keyboard accepts.
    private const int ColorTextLength = 9;

    /// <summary>Loads the compiled page XAML.</summary>
    public DeviceProfilesPage()
    {
        InitializeComponent();
        // The editor reports the edited curve; the row holds it and the view model records that the
        // profile list is dirty. Without the last part a curve edit is discarded at save, because
        // profiles are only written when this window actually changed them.
        ProfileCurve.CurveChanged += OnCurveChanged;
    }

    private void OnCurveChanged(IReadOnlyList<CurvePoint> curve)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedDeviceProfile is { } profile)
        {
            profile.Curve = curve;
        }

        viewModel.NoteDeviceProfileEdited();
    }

    private void OnAddProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            // Software fan policy applies this authored shape to the active logical fans.
            viewModel.AddDeviceProfile(CapabilityIds.FanCurve);
        }
    }

    private void OnAddColorProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.AddDeviceProfile(CapabilityIds.LightingColor, true);
        }
    }

    private void OnRemoveProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.RemoveSelectedDeviceProfile();
        }
    }

    private void OnEditProfileName(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { SelectedDeviceProfile: { } profile }
            && TopLevel.GetTopLevel(this) is SettingsWindow window)
        {
            window.ShowOnScreenKeyboard(
                profile.Name,
                0,
                "Device profile name",
                value =>
                {
                    profile.Name = value;
                    return null;
                });
        }
    }

    private void OnEditProfileColor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { SelectedDeviceProfile: { } profile }
            && TopLevel.GetTopLevel(this) is SettingsWindow window)
        {
            ShowColorKeyboard(window, profile.ColorHex, "Device profile colour", value => profile.ColorHex = value);
        }
    }

    private static void ShowColorKeyboard(
        SettingsWindow window,
        string initial,
        string title,
        Action<string> apply)
    {
        window.ShowOnScreenKeyboard(initial, ColorTextLength, title, value =>
        {
            if (!Color.TryParse(value, out _))
            {
                return "Enter a color such as #FF9D3D.";
            }

            apply(value);
            return null;
        });
    }
}
