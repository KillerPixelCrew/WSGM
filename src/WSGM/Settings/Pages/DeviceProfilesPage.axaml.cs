using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WSGM.Controls;
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
        ProfileCurve.PropertyChanged += (_, e) =>
        {
            if (e.Property == CurveEditor.SelectedIndexProperty || e.Property == CurveEditor.PointsProperty)
            {
                ShowPicked();
            }
        };
    }

    /// <summary>Says which point is picked and where it sits, so the controller edit reads without the plot.</summary>
    private void ShowPicked()
    {
        var points = ProfileCurve.Points;
        var index = ProfileCurve.SelectedIndex;
        PointReadout.Text = index >= 0 && index < points.Count
            ? $"Point {index + 1}:  {points[index].Input} °C  →  {points[index].Output} % fan"
            : $"{points.Count} points";
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

    /// <summary>Asks before removing: the confirmation shows in place, with Keep focused.</summary>
    private void OnRemoveProfile(object? sender, RoutedEventArgs e)
    {
        RemoveConfirm.IsVisible = true;
        Dispatcher.UIThread.Post(() => KeepProfile.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
    }

    private void OnConfirmRemove(object? sender, RoutedEventArgs e)
    {
        RemoveConfirm.IsVisible = false;
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.RemoveSelectedDeviceProfile();
        }

        ProfileList.Focus(NavigationMethod.Directional);
    }

    private void OnKeepProfile(object? sender, RoutedEventArgs e)
    {
        RemoveConfirm.IsVisible = false;
        RemoveProfile.Focus(NavigationMethod.Directional);
    }

    private void OnResetCurve(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { SelectedDeviceProfile: { IsCurveProfile: true } profile } viewModel)
        {
            profile.Curve = SettingsViewModel.DefaultFanCurve;
            viewModel.NoteDeviceProfileEdited();
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
