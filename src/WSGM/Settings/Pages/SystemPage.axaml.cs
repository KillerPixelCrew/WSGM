using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace WSGM.Settings.Pages;

/// <summary>
///     The System settings page: shell status hero row, sign-in start, Steam start,
///     device-manager takeover and start mode, updates, recovery help, power and diagnostics.
///     Inherits the window's <see cref="SettingsViewModel" /> DataContext.
/// </summary>
public partial class SystemPage : UserControl
{
    private SettingsViewModel? _model;

    /// <summary>Loads the compiled page XAML.</summary>
    public SystemPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelPropertyChanged;
            }

            _model = DataContext as SettingsViewModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelPropertyChanged;
            }
        };
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.LogUploadConfirmPending)
            or nameof(SettingsViewModel.LogUploadBusy))
        {
            // After the bindings have shown and hidden the row's buttons.
            Dispatcher.UIThread.Post(MoveLogUploadFocus);
        }
    }

    // The log upload swaps the row's buttons as it moves on, and the one a controller user just pressed is often
    // the one that disappears. Focus follows to the safe answer while confirming or uploading, then to the next
    // useful action, but only when it was in this row or had nowhere left to be.
    private void MoveLogUploadFocus()
    {
        if (_model is not { } model || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        // Hiding the focused button leaves focus nowhere, or on the window itself; neither is a place to keep.
        if (top.FocusManager?.GetFocusedElement() is Visual focused && focused != top
                                                                    && !LogRow.IsVisualAncestorOf(focused)
                                                                    && focused is InputElement
                                                                    {
                                                                        IsEffectivelyVisible: true,
                                                                        IsEffectivelyEnabled: true
                                                                    })
        {
            return;
        }

        var target = model.CanCancelLogUpload
            ? CancelLogUploadButton
            : model.CanCopyLogLinks
                ? CopyLogLinksButton
                : UploadLogButton;
        target.Focus(NavigationMethod.Directional);
    }
}
