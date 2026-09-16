using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>Offers explicit mode selection and apply through Windows Device Control.</summary>
internal sealed class DisplayModeView : StackPanel
{
    private readonly CardButton _apply = new()
        { Title = "Apply display mode", IconGeometry = Icons.Monitor, IsEnabled = false };

    private readonly Func<Task<DisplayModeSnapshot?>> _read;
    private readonly ComboBox _refresh = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _resolution = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _busy;
    private bool _closed;
    private DisplayModeSnapshot? _snapshot;
    private bool _synchronizing;

    internal DisplayModeView(Func<Task<DisplayModeSnapshot?>>? read = null)
    {
        _read = read ?? (() => Task.Run(() =>
        {
            var paths = DisplayTopology.CaptureActive().Paths;
            return paths.Count == 0 ? null : DisplayModes.Read(paths[0].Target);
        }));
        Classes.Add("overlay-control");
        Spacing = 8;
        Children.Add(new TextBlock { Text = "Display mode", Classes = { "setting-title" } });
        Children.Add(_status);
        Children.Add(Selector("Resolution", _resolution));
        Children.Add(Selector("Refresh rate", _refresh));
        Children.Add(_apply);
        _refresh.ItemTemplate = new FuncDataTemplate<int>((hz, _) => new TextBlock { Text = $"{hz} Hz" });
        _resolution.SelectionChanged += (_, _) =>
        {
            if (!_synchronizing)
            {
                UpdateRates();
            }
        };
        _apply.Click += async (_, _) => await ApplyAsync();
        // A hidden page keeps its controls in the tree for the sheet's life; skip the tick there.
        _timer.Tick += async (_, _) =>
        {
            if (this.GetVisualParent() is { IsEffectivelyVisible: false })
            {
                return;
            }

            await ReadAsync();
        };
        AttachedToVisualTree += async (_, _) =>
        {
            _timer.Start();
            await ReadAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
        };
    }

    private static Border Selector(string label, ComboBox selector)
    {
        AutomationProperties.SetName(selector, label);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,240"), ColumnSpacing = 12 };
        grid.Children.Add(new TextBlock
            { Text = label, Classes = { "setting-title" }, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(selector, 1);
        grid.Children.Add(selector);
        return new Border { Classes = { "tile" }, Child = grid };
    }

    private static string Resolution(DisplayMode mode)
    {
        return $"{mode.Width} × {mode.Height}";
    }

    private void UpdateRates()
    {
        if (_snapshot is null)
        {
            return;
        }

        var rates = _snapshot.Supported.Where(mode => Resolution(mode) == _resolution.SelectedItem as string)
            .Select(mode => mode.RefreshHz).Distinct().ToArray();
        var selected = _refresh.SelectedItem as int?;
        _refresh.ItemsSource = rates;
        _refresh.SelectedItem = selected is { } hz && rates.Contains(hz) ? hz
            : rates.Contains(_snapshot.Current.RefreshHz) ? _snapshot.Current.RefreshHz : rates.FirstOrDefault();
    }

    private async Task ReadAsync()
    {
        if (_busy || _closed)
        {
            return;
        }

        _busy = true;
        try
        {
            var next = await _read();
            if (_closed)
            {
                return;
            }

            var changed = _snapshot?.Path != next?.Path || _snapshot?.Current != next?.Current
                                                        || !(_snapshot?.Supported.SequenceEqual(next?.Supported ??
                                                            []) ?? next is null);
            _snapshot = next;
            _apply.IsEnabled = next is not null && next.Supported.Count > 0;
            _resolution.IsEnabled = _refresh.IsEnabled = _apply.IsEnabled;
            if (next is null)
            {
                _status.Text = "Display modes unavailable";
                return;
            }

            if (!changed)
            {
                return;
            }

            _status.Text = $"{next.Path.Target.FriendlyName}: {Resolution(next.Current)}, {next.Current.RefreshHz} Hz";
            _synchronizing = true;
            _resolution.ItemsSource = next.Supported.Select(Resolution).Distinct().ToArray();
            _resolution.SelectedItem = Resolution(next.Current);
            UpdateRates();
            _refresh.SelectedItem = next.Current.RefreshHz;
            _synchronizing = false;
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _snapshot = null;
                _status.Text = "Display unavailable: " + ex.Message;
                _apply.IsEnabled = _resolution.IsEnabled = _refresh.IsEnabled = false;
            }
        }
        finally
        {
            _busy = false;
            _synchronizing = false;
        }
    }

    private async Task ApplyAsync()
    {
        if (_busy || _closed || _snapshot is not { } snapshot)
        {
            return;
        }

        var requested = snapshot.Supported.FirstOrDefault(mode =>
            Resolution(mode) == _resolution.SelectedItem as string && mode.RefreshHz == _refresh.SelectedItem as int?);
        if (requested is null)
        {
            return;
        }

        _busy = true;
        _apply.IsEnabled = false;
        string detail;
        try
        {
            detail = (await Task.Run(() => DisplayModes.Apply(snapshot, requested))).Detail;
        }
        catch (Exception ex)
        {
            detail = "Display change was not confirmed: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }

        await ReadAsync();
        if (!_closed)
        {
            _status.Text += "\n" + detail;
        }
    }
}
