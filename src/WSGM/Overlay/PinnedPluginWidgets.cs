using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Retains pinned plugin controls on the front page, including missing-provider placeholders.</summary>
internal sealed class PinnedPluginWidgets : StackPanel
{
    internal PinnedPluginWidgets(ICommonPluginOverlaySource source, Action<PluginWidgetPin, string> navigate,
        PluginWidgetPreferences? preferences = null)
    {
        preferences ??= PluginWidgetPreferences.Default;
        Spacing = 8;
        Focusable = true;
        PluginWidgetPin[] previous = [];
        (PluginWidgetPin? Pin, string Label, int Index)? pendingFocus = null;
        bool reading = false, closed = false;
        TextBlock error = new();
        async Task RefreshAsync()
        {
            if (reading || closed) { return; }
            reading = true;
            try
            {
                var pins = await preferences.Read();
                if (closed || previous.SequenceEqual(pins)) { return; }
                previous = pins;
                Children.Clear();
                Children.Add(error);
                foreach (var pin in pins)
                {
                    StackPanel card = new() { Spacing = 4 };
                    card.Children.Add(new TextBlock { Text = $"{pin.PluginId} / {pin.InstanceId} / {pin.WidgetId}", Classes = { "caption" } });
                    card.Children.Add(new CommonPluginPanel(source, pin, navigate));
                    StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
                    void Add(string label, Func<Task> action)
                    {
                        Button button = new() { Content = label, Tag = (pin, label) };
                        button.Click += async (_, _) =>
                        {
                            button.IsEnabled = false;
                            try
                            {
                                int index = Array.IndexOf(previous, pin);
                                await action();
                                button.IsEnabled = true;
                                pendingFocus = (pin, label, index);
                                await RefreshAsync();
                            }
                            catch (Exception ex) { error.Text = ex.Message; }
                            finally { button.IsEnabled = true; }
                        };
                        actions.Children.Add(button);
                    }
                    Add("Move up", () => preferences.Move(pin, -1));
                    Add("Move down", () => preferences.Move(pin, 1));
                    Add("Unpin", () => preferences.Remove(pin));
                    card.Children.Add(actions);
                    Children.Add(card);
                }
                if (pins.Length > 0)
                {
                    Button reset = new() { Content = "Reset widget order", Tag = "reset" };
                    reset.Click += async (_, _) =>
                    {
                        try
                        {
                            await preferences.Reset();
                            pendingFocus = (null, "reset", 0);
                            await RefreshAsync();
                        }
                        catch (Exception ex) { error.Text = ex.Message; }
                    };
                    Children.Add(reset);
                }
            }
            catch (Exception ex) { error.Text = ex.Message; }
            finally
            {
                reading = false;
                if (!closed && pendingFocus is { } target)
                {
                    pendingFocus = null;
                    var buttons = this.GetLogicalDescendants().OfType<Button>().ToArray();
                    var button = target.Pin is null ? buttons.FirstOrDefault(item => Equals(item.Tag, "reset"))
                        : buttons.FirstOrDefault(item => Equals(item.Tag, (target.Pin, target.Label)));
                    if (button is null && previous.Length > 0)
                    {
                        var neighbor = previous[Math.Clamp(target.Index, 0, previous.Length - 1)];
                        button = buttons.FirstOrDefault(item => Equals(item.Tag, (neighbor, "Unpin")));
                    }
                    Control destination = button is null ? this : button;
                    destination.BringIntoView();
                    destination.Focus();
                }
            }
        }
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => await RefreshAsync();
        AttachedToVisualTree += async (_, _) => { timer.Start(); await RefreshAsync(); };
        DetachedFromVisualTree += (_, _) => { closed = true; timer.Stop(); };
    }
}
