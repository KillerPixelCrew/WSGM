using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using WindowsDeviceControl;

namespace WSGM.Overlay;

/// <summary>Offers explicit mode selection and apply through Windows Device Control.</summary>
internal sealed class DisplayModeView : StackPanel
{
    private readonly TextBlock _status = new();
    private readonly ComboBox _resolution = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly ComboBox _refresh = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly Button _apply = new() { Content = "Apply display mode", IsEnabled = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DisplayModeSnapshot? _snapshot;
    private bool _busy;
    private bool _closed;
    private bool _synchronizing;
    private readonly Func<Task<DisplayModeSnapshot?>> _read;

    internal DisplayModeView(Func<Task<DisplayModeSnapshot?>>? read = null)
    {
        _read = read ?? (() => Task.Run(() =>
        {
            var path = DisplayTopology.CaptureActive().Paths.FirstOrDefault();
            return path is null ? null : DisplayModes.Read(path.Target);
        }));
        Spacing = 6;
        Children.Add(new TextBlock { Text = "Display mode", Classes = { "setting-title" } });
        Children.Add(_status);
        Children.Add(_resolution);
        Children.Add(_refresh);
        Children.Add(_apply);
        _resolution.SelectionChanged += (_, _) => { if (!_synchronizing) { UpdateRates(); } };
        _apply.Click += async (_, _) => await ApplyAsync();
        _timer.Tick += async (_, _) => await ReadAsync();
        AttachedToVisualTree += async (_, _) => { _timer.Start(); await ReadAsync(); };
        DetachedFromVisualTree += (_, _) => { _closed = true; _timer.Stop(); };
    }

    private static string Resolution(DisplayMode mode) => $"{mode.Width} × {mode.Height}";

    private void UpdateRates()
    {
        if (_snapshot is null) { return; }
        var rates = _snapshot.Supported.Where(mode => Resolution(mode) == _resolution.SelectedItem as string)
            .Select(mode => mode.RefreshHz).Distinct().ToArray();
        int? selected = _refresh.SelectedItem as int?;
        _refresh.ItemsSource = rates;
        _refresh.SelectedItem = selected is { } hz && rates.Contains(hz) ? hz
            : rates.Contains(_snapshot.Current.RefreshHz) ? _snapshot.Current.RefreshHz : rates.FirstOrDefault();
    }

    private async Task ReadAsync()
    {
        if (_busy || _closed) { return; }
        _busy = true;
        try
        {
            var next = await _read();
            if (_closed) { return; }
            bool changed = _snapshot?.Path != next?.Path || _snapshot?.Current != next?.Current
                || !(_snapshot?.Supported.SequenceEqual(next?.Supported ?? []) ?? next is null);
            _snapshot = next;
            _apply.IsEnabled = next is not null && next.Supported.Count > 0;
            _resolution.IsEnabled = _refresh.IsEnabled = _apply.IsEnabled;
            if (next is null) { _status.Text = "Display modes unavailable"; return; }
            if (!changed) { return; }
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
        finally { _busy = false; _synchronizing = false; }
    }

    private async Task ApplyAsync()
    {
        if (_busy || _closed || _snapshot is not { } snapshot) { return; }
        var requested = snapshot.Supported.FirstOrDefault(mode =>
            Resolution(mode) == _resolution.SelectedItem as string && mode.RefreshHz == _refresh.SelectedItem as int?);
        if (requested is null) { return; }
        _busy = true;
        _apply.IsEnabled = false;
        string detail;
        try { detail = (await Task.Run(() => DisplayModes.Apply(snapshot, requested))).Detail; }
        catch (Exception ex) { detail = "Display change was not confirmed: " + ex.Message; }
        finally { _busy = false; }
        await ReadAsync();
        if (!_closed) { _status.Text += "\n" + detail; }
    }
}
