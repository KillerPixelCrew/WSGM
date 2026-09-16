using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
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
        Width = 600;
        HorizontalAlignment = HorizontalAlignment.Left;
        Focusable = true;
        PluginWidgetPin[] previous = [];
        (PluginWidgetPin? Pin, string Label, int Index)? pendingFocus = null;
        bool reading = false, closed = false;
        TextBlock error = new() { IsVisible = false, Classes = { "caption" } };
        async Task RefreshAsync()
        {
            if (reading || closed) { return; }
            reading = true;
            try
            {
                var pins = await preferences.Read();
                if (closed || previous.SequenceEqual(pins)) { return; }
                previous = pins;
                var expanded = Children.OfType<StackPanel>().Where(card => card.Children.OfType<Expander>().Any(e => e.IsExpanded))
                    .Select(card => card.Tag).ToHashSet();
                Children.Clear();
                Children.Add(error);
                foreach (var pin in pins)
                {
                    StackPanel card = new() { Spacing = 4, Tag = pin };

                    card.Children.Add(new CommonPluginPanel(source, pin, navigate));
                    StackPanel actions = new() { Spacing = 4 };
                    void Add(string label, Func<Task> action)
                    {
                        CardButton button = new() { Title = label, Tag = (pin, label), IconGeometry = label == "Unpin" ? Icons.Pin : Icons.Restart };
                        button.IsEnabled = label != "Move up" || pin != pins[0];
                        if (label == "Move down" && pin == pins[^1]) { button.IsEnabled = false; }
                        button.Click += async (_, _) =>
                        {
                            button.IsEnabled = false;
                            try
                            {
                                var index = Array.IndexOf(previous, pin);
                                await action();
                                button.IsEnabled = true;
                                pendingFocus = (pin, label, index);
                                await RefreshAsync();
                            }
                            catch (Exception ex) { error.Text = ex.Message; error.IsVisible = true; }
                            finally { button.IsEnabled = true; }
                        };
                        actions.Children.Add(button);
                    }
                    Add("Move up", () => preferences.Move(pin, -1));
                    Add("Move down", () => preferences.Move(pin, 1));
                    Add("Unpin", () => preferences.Remove(pin));
                    card.Children.Add(new Expander
                    {
                        Header = "Arrange widget",
                        Content = actions,
                        IsExpanded = expanded.Contains(pin) || pendingFocus is not null,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch
                    });
                    Children.Add(card);
                }
                if (pins.Length > 0)
                {
                    CardButton reset = new() { Title = "Reset widget order", Tag = "reset", IconGeometry = Icons.Restart };
                    reset.Click += async (_, _) =>
                    {
                        try
                        {
                            await preferences.Reset();
                            pendingFocus = (null, "reset", 0);
                            await RefreshAsync();
                        }
                        catch (Exception ex) { error.Text = ex.Message; error.IsVisible = true; }
                    };
                    Children.Add(new Expander
                    {
                        Header = "Widget order",
                        Content = reset,
                        IsExpanded = pendingFocus?.Pin is null && pendingFocus is not null,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch
                    });
                }
            }
            catch (Exception ex) { error.Text = ex.Message; error.IsVisible = true; }
            finally
            {
                reading = false;
                if (!closed && pendingFocus is { } target)
                {
                    pendingFocus = null;
                    var buttons = this.GetLogicalDescendants().OfType<Button>().ToArray();
                    var button = target.Pin is null ? buttons.FirstOrDefault(item => Equals(item.Tag, "reset"))
                        : buttons.FirstOrDefault(item => Equals(item.Tag, (target.Pin, target.Label)) && item.IsEnabled);
                    button ??= buttons.FirstOrDefault(item => Equals(item.Tag, (target.Pin, "Unpin")));
                    if (button is null && previous.Length > 0)
                    {
                        var neighbor = previous[Math.Clamp(target.Index, 0, previous.Length - 1)];
                        button = buttons.FirstOrDefault(item => Equals(item.Tag, (neighbor, "Unpin")));
                    }
                    Control destination = button is null ? this : button;
                    UpdateLayout();
                    destination.BringIntoView();
                    destination.Focus();
                }
            }
        }
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        // A hidden page keeps its controls in the tree for the sheet's life; skip the tick there.
        timer.Tick += async (_, _) => { if (this.GetVisualParent() is { IsEffectivelyVisible: false }) { return; } await RefreshAsync(); };
        AttachedToVisualTree += async (_, _) => { timer.Start(); await RefreshAsync(); };
        DetachedFromVisualTree += (_, _) => { closed = true; timer.Stop(); };
    }
}
