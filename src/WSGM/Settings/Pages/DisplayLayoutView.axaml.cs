using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Settings.Pages;

/// <summary>Projects one draft as an arrangement, a display list and an inspector.</summary>
public partial class DisplayLayoutView : UserControl
{
    /// <summary>Loads the layout editor.</summary>
    public DisplayLayoutView()
    {
        InitializeComponent();
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
