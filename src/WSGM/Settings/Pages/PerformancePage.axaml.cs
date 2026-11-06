using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace WSGM.Settings.Pages;

/// <summary>The Performance section: RTSS controls, overlay widgets and graphics drivers.</summary>
/// <remarks>
///     A widget row moves three ways: drag its grip, press its grip (A, Enter or Space) or Y anywhere on the row to
///     pick it up and then Up or Down, or press Y, A or B again to put it down.
/// </remarks>
public partial class PerformancePage : UserControl
{
    private OsdWidgetRow? _dragging;
    private OsdWidgetRow? _moving;

    /// <summary>Loads the compiled page XAML.</summary>
    public PerformancePage()
    {
        InitializeComponent();
        WidgetList.AddHandler(PointerPressedEvent, OnWidgetPointerPressed, RoutingStrategies.Tunnel);
        WidgetList.PointerMoved += OnWidgetPointerMoved;
        WidgetList.PointerReleased += OnWidgetPointerReleased;
        WidgetList.PointerCaptureLost += (_, _) => _dragging = null;
    }

    private SettingsViewModel? Model => DataContext as SettingsViewModel;

    /// <summary>Whether the focused element is on a widget row, where Y moves the row rather than searching.</summary>
    /// <param name="focused">The focused element.</param>
    internal static bool IsWidgetRow(IInputElement? focused)
    {
        return focused is Visual { DataContext: OsdWidgetRow, IsEffectivelyVisible: true };
    }

    /// <summary>Picks up the focused widget row, or puts down the one that is picked up.</summary>
    /// <param name="focused">The focused element.</param>
    /// <returns>False when the focus is not on a widget row.</returns>
    internal bool ToggleMove(IInputElement? focused)
    {
        if (focused is not StyledElement { DataContext: OsdWidgetRow row })
        {
            return false;
        }

        if (_moving == row)
        {
            EndMove();
            return true;
        }

        _moving?.IsMoving = false;
        _moving = row;
        row.IsMoving = true;
        return true;
    }

    /// <summary>Puts down the picked-up row, if there is one.</summary>
    /// <returns>Whether a row was picked up.</returns>
    internal bool EndMove()
    {
        if (_moving is not { } row)
        {
            return false;
        }

        row.IsMoving = false;
        _moving = null;
        FocusGrip(row);
        return true;
    }

    /// <summary>Moves the picked-up row with Up and Down; other directions stay on it.</summary>
    /// <param name="direction">The direction pressed.</param>
    /// <returns>Whether a row is picked up, so the direction is used here.</returns>
    internal bool MovePicked(NavigationDirection direction)
    {
        if (_moving is not { } row || Model is not { } model)
        {
            return false;
        }

        if (direction is NavigationDirection.Up or NavigationDirection.Down
            && model.MoveOsdWidget(row, direction == NavigationDirection.Up ? -1 : 1))
        {
            FocusGrip(row);
        }

        return true;
    }

    private void OnGripClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ToggleMove(sender as IInputElement);
    }

    private void OnWidgetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.FindAncestorOfType<Button>(includeSelf: true) is not { DataContext: OsdWidgetRow row } grip
            || !grip.Classes.Contains("settings-grip"))
        {
            return;
        }

        // The list holds the capture: a moved row's container is rebuilt, which would end a capture on the grip.
        e.Handled = true;
        EndMove();
        _dragging = row;
        e.Pointer.Capture(WidgetList);
    }

    private void OnWidgetPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is not { } row || Model is not { } model)
        {
            return;
        }

        var y = e.GetPosition(WidgetList).Y;
        var target = model.OsdWidgets.IndexOf(row);
        for (var index = 0; index < model.OsdWidgets.Count; index++)
        {
            if (WidgetList.ContainerFromIndex(index) is { } container && y < container.Bounds.Bottom)
            {
                target = index;
                break;
            }

            target = index;
        }

        model.MoveOsdWidget(row, target - model.OsdWidgets.IndexOf(row));
    }

    private void OnWidgetPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging is null)
        {
            return;
        }

        _dragging = null;
        e.Pointer.Capture(null);
    }

    private void FocusGrip(OsdWidgetRow row)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (WidgetList.ContainerFromItem(row) is not { } container)
            {
                return;
            }

            container.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus(NavigationMethod.Directional);
            container.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
