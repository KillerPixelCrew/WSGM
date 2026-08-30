using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Settings.Pages;

/// <summary>Settings the installed device plugin declares, rendered from its manifest.</summary>
/// <remarks>
/// The page owns no controls of its own. Its content comes from the one projection in
/// <c>PluginSettingsCoordinator.Project</c>, shared with the overlay so both surfaces order and
/// place a plugin's settings identically, and it changes with whichever plugin is installed — which
/// is why the sections and rows are bound rather than written here.
/// </remarks>
public partial class PluginSettingsPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public PluginSettingsPage()
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
            // The fan curve is the only curve capability WSGM has a semantic role for, so it is the
            // one a new profile authors until a plugin publishes another.
            viewModel.AddDeviceProfile(FanCurveCapabilityId);
        }
    }

    private void OnRemoveProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.RemoveSelectedDeviceProfile();
        }
    }

    /// <summary>The capability a newly authored curve profile targets.</summary>
    private const string FanCurveCapabilityId = "thermal.fan-curve";
}
