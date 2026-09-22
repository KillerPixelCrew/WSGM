using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>Projects manual power mode; selection saves policy without applying a wattage.</summary>
internal sealed class ManualTdpModeView : StackPanel
{
    internal ManualTdpModeView(Func<(bool Available, bool Unified)> read, Func<bool, Task> save,
        Func<string?>? overrideId = null, Func<string, Task>? useGlobal = null)
    {
        Spacing = 6;
        ComboBox choice = new() { ItemsSource = new[] { "Advanced / split TDP", "Unified TDP" } };
        TextBlock status = new()
        {
            Text = "Unified TDP coordinates sustained and boost limits through the device plugin.",
            TextWrapping = TextWrapping.Wrap
        };
        ProfileOverrideMarker marker = new(choice, useGlobal);
        Children.Add(marker);
        Children.Add(status);
        bool rendering = false, writing = false, closed = false;
        choice.SelectionChanged += async (_, _) =>
        {
            if (rendering || writing || closed || choice.SelectedIndex < 0)
            {
                return;
            }

            writing = true;
            choice.IsEnabled = false;
            try
            {
                await save(choice.SelectedIndex == 1);
                status.Text = "Mode saved. Adjust the sustained TDP slider to apply a target.";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
            finally
            {
                writing = false;
                Refresh();
            }
        };
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        // A hidden page keeps its controls in the tree for the sheet's life; skip the tick there.
        timer.Tick += (_, _) =>
        {
            if (this.GetVisualParent() is { IsEffectivelyVisible: false })
            {
                return;
            }

            Refresh();
        };
        AttachedToVisualTree += (_, _) =>
        {
            closed = false;
            Refresh();
            timer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            closed = true;
            timer.Stop();
        };
        return;

        void Refresh()
        {
            if (writing || closed)
            {
                return;
            }

            var state = read();
            rendering = true;
            IsVisible = state.Available;
            choice.IsEnabled = state.Available;
            choice.SelectedIndex = state.Unified ? 1 : 0;
            marker.Refresh(overrideId?.Invoke());
            rendering = false;
        }
    }
}
