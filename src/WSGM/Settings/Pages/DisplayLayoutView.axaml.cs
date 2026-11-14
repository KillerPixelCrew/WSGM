using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace WSGM.Settings.Pages;

/// <summary>Projects one draft as an arrangement, a display list and an inspector.</summary>
public partial class DisplayLayoutView : UserControl
{
    /// <summary>Loads the layout editor.</summary>
    public DisplayLayoutView()
    {
        InitializeComponent();
    }

    private void OnRequestForget(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DisplayLayoutEditorRow row
            && DataContext is DisplayLayoutEditor editor)
        {
            foreach (var other in editor.Rows)
            {
                other.ConfirmForget = other == row;
            }

            FocusDisplayAction(row, "keep-display");
            e.Handled = true;
        }
    }

    private void OnKeepDisplay(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DisplayLayoutEditorRow row)
        {
            row.ConfirmForget = false;
            FocusDisplayAction(row, "request-forget");
            e.Handled = true;
        }
    }

    private void OnConfirmForget(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DisplayLayoutEditorRow row
            && this.FindAncestorOfType<DisplayPage>()?.DataContext is SettingsViewModel model)
        {
            model.ForgetDisplayCommand.Execute(row);
            Dispatcher.UIThread.Post(() =>
            {
                var target = DisplayList.SelectedItem is { } selected
                    ? DisplayList.ContainerFromItem(selected) : null;
                (target ?? DisplayList).Focus(NavigationMethod.Directional);
            }, DispatcherPriority.Loaded);
            e.Handled = true;
        }
    }

    private void FocusDisplayAction(DisplayLayoutEditorRow row, string tag)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DisplayList.ContainerFromItem(row)?.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => Equals(button.Tag, tag))?.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Makes the edited display the primary one; the editor moves the others off primary.</summary>
    private void OnMakePrimary(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DisplayLayoutEditorRow row)
        {
            row.IsPrimary = true;
        }
    }

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        (DataContext as DisplayLayoutEditor)?.Undo();
    }

    private void OnPositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: > 0 } choice || DataContext is not DisplayLayoutEditor editor)
        {
            return;
        }

        editor.PlaceSelected(choice.SelectedIndex);
        choice.SelectedIndex = 0;
    }
}
