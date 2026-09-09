using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Retains pinned plugin controls on the front page, including missing-provider placeholders.</summary>
internal sealed class PinnedPluginWidgets : StackPanel
{
    internal PinnedPluginWidgets(CommonPluginOverlaySource source)
    {
        Spacing = 8;
        PluginWidgetPin[] previous = [];
        bool reading = false, closed = false;
        TextBlock error = new();
        async Task RefreshAsync()
        {
            if (reading || closed) { return; }
            reading = true;
            try
            {
                var pins = await Task.Run(() => ConfigStore.Load().PluginWidgetPins.ToArray());
                if (closed || previous.SequenceEqual(pins)) { return; }
                previous = pins;
                Children.Clear();
                Children.Add(error);
                foreach (var pin in pins)
                {
                    StackPanel card = new() { Spacing = 4 };
                    card.Children.Add(new TextBlock { Text = $"{pin.PluginId} / {pin.InstanceId} / {pin.WidgetId}", Classes = { "caption" } });
                    card.Children.Add(new CommonPluginPanel(source, pin));
                    StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
                    void Add(string label, Func<Task> action)
                    {
                        Button button = new() { Content = label };
                        button.Click += async (_, _) =>
                        {
                            button.IsEnabled = false;
                            try { await action(); await RefreshAsync(); }
                            catch (Exception ex) { error.Text = ex.Message; }
                            finally { button.IsEnabled = true; }
                        };
                        actions.Children.Add(button);
                    }
                    Add("Move up", () => CommonPluginOverlaySource.MovePinAsync(pin, -1));
                    Add("Move down", () => CommonPluginOverlaySource.MovePinAsync(pin, 1));
                    Add("Unpin", () => CommonPluginOverlaySource.SetPinnedAsync(pin, false));
                    card.Children.Add(actions);
                    Children.Add(card);
                }
                if (pins.Length > 0)
                {
                    Button reset = new() { Content = "Reset widget order" };
                    reset.Click += async (_, _) =>
                    {
                        try { await CommonPluginOverlaySource.ResetPinOrderAsync(); await RefreshAsync(); }
                        catch (Exception ex) { error.Text = ex.Message; }
                    };
                    Children.Add(reset);
                }
            }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { reading = false; }
        }
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => await RefreshAsync();
        AttachedToVisualTree += async (_, _) => { timer.Start(); await RefreshAsync(); };
        DetachedFromVisualTree += (_, _) => { closed = true; timer.Stop(); };
    }
}
