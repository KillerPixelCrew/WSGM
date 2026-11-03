using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace WSGM.Settings.Pages;

/// <summary>The Diagnostics section: the log, its upload, verbose logging, recovery and the last standby.</summary>
public partial class DiagnosticsPage : UserControl
{
    private SettingsViewModel? _model;

    /// <summary>Loads the compiled page XAML.</summary>
    public DiagnosticsPage()
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
            : model.CanCopyLogLink
                ? CopyLogLinkButton
                : UploadLogButton;
        target.Focus(NavigationMethod.Directional);
    }
}
