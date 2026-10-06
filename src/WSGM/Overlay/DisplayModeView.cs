using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
///     Reports committed mode selections to the composition for the display the sheet is shown on.
/// </summary>
internal sealed class DisplayModeView : StackPanel
{
    private readonly Func<DisplayModeSnapshot, DisplayMode, Task<DisplayModeResult>> _apply;
    private readonly Func<string?> _display;

    private readonly Func<string?, Task<DisplayModeSnapshot?>> _read;
    private readonly ComboBox _refresh = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _resolution = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new();
    private int _attachmentGeneration;
    private bool _busy;
    private bool _closed;
    private bool _readPending;
    private DisplayModeSnapshot? _snapshot;
    private bool _synchronizing;

    /// <summary>Creates the selector.</summary>
    /// <param name="read">
    ///     Reads the modes of the display with the given GDI source name, null when the sheet's display
    ///     is unknown.
    /// </param>
    /// <param name="apply">Applies a mode to the display a snapshot was read from.</param>
    /// <param name="display">The sheet's current GDI display name, resolved again on every read.</param>
    internal DisplayModeView(Func<string?, Task<DisplayModeSnapshot?>> read,
        Func<DisplayModeSnapshot, DisplayMode, Task<DisplayModeResult>> apply,
        Func<string?> display)
    {
        _display = display;
        _read = read;
        _apply = apply;
        Classes.Add("overlay-control");
        Spacing = 8;
        Children.Add(new TextBlock { Text = "Display mode", Classes = { "setting-title" } });
        Children.Add(_status);
        Children.Add(Selector("Resolution", _resolution));
        Children.Add(Selector("Refresh rate", _refresh));
        _refresh.ItemTemplate = new FuncDataTemplate<int>((hz, _) => new TextBlock { Text = $"{hz} Hz" });
        _resolution.SelectionChanged += async (_, _) =>
        {
            if (!_synchronizing)
            {
                UpdateRates();
                if (!_resolution.IsDropDownOpen)
                {
                    await ApplyAsync();
                }
            }
        };
        _refresh.SelectionChanged += async (_, _) =>
        {
            if (!_synchronizing && !_refresh.IsDropDownOpen)
            {
                await ApplyAsync();
            }
        };
        _resolution.DropDownClosed += async (_, _) => await ApplyAsync();
        _refresh.DropDownClosed += async (_, _) => await ApplyAsync();
        AttachedToVisualTree += (_, _) =>
        {
            _closed = false;
            _attachmentGeneration++;
        };
        // An open selector is mid-choice; a re-read would replace the list under it.
        VisiblePoll.Attach(this, TimeSpan.FromSeconds(5), async () =>
        {
            if (!_resolution.IsDropDownOpen && !_refresh.IsDropDownOpen)
            {
                await ReadAsync();
            }
        });
        DetachedFromVisualTree += (_, _) =>
        {
            _closed = true;
            _attachmentGeneration++;
        };
    }

    private static Border Selector(string label, ComboBox selector)
    {
        AutomationProperties.SetName(selector, label);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,180"), ColumnSpacing = 12 };
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
        var wasSynchronizing = _synchronizing;
        _synchronizing = true;
        _refresh.ItemsSource = rates;
        _refresh.SelectedItem = selected is { } hz && rates.Contains(hz) ? hz
            : rates.Contains(_snapshot.Current.RefreshHz) ? _snapshot.Current.RefreshHz : rates.FirstOrDefault();
        _synchronizing = wasSynchronizing;
    }

    private async Task ReadAsync()
    {
        if (_closed)
        {
            return;
        }

        if (_busy)
        {
            _readPending = true;
            return;
        }

        var generation = _attachmentGeneration;
        _busy = true;
        _resolution.IsEnabled = _refresh.IsEnabled = false;
        try
        {
            // Read per refresh, so a sheet placed on another display follows it.
            var display = _display();
            var next = await _read(display);
            if (_closed || generation != _attachmentGeneration)
            {
                return;
            }

            if (!string.Equals(display, _display(), StringComparison.OrdinalIgnoreCase))
            {
                _readPending = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(display) || !string.Equals(next?.Path.SourceName, display,
                    StringComparison.OrdinalIgnoreCase))
            {
                next = null;
            }

            var changed = _snapshot?.Path != next?.Path || _snapshot?.Current != next?.Current
                                                        || !(_snapshot?.Supported.SequenceEqual(next?.Supported ??
                                                            []) ?? next is null);
            _snapshot = next;
            _resolution.IsEnabled = _refresh.IsEnabled = next is not null && next.Supported.Count > 0;
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
            if (!_closed && generation == _attachmentGeneration)
            {
                _snapshot = null;
                _status.Text = "Display unavailable: " + ex.Message;
                _resolution.IsEnabled = _refresh.IsEnabled = false;
            }
        }
        finally
        {
            _busy = false;
            _synchronizing = false;
            if (_readPending && !_closed)
            {
                _readPending = false;
                _ = ReadAsync();
            }
        }
    }

    private async Task ApplyAsync()
    {
        if (_busy || _closed || _synchronizing || _snapshot is not { } snapshot)
        {
            return;
        }

        if (!string.Equals(snapshot.Path.SourceName, _display(), StringComparison.OrdinalIgnoreCase))
        {
            _snapshot = null;
            await ReadAsync();
            return;
        }

        var requested = snapshot.Supported.FirstOrDefault(mode =>
            Resolution(mode) == _resolution.SelectedItem as string && mode.RefreshHz == _refresh.SelectedItem as int?);
        if (requested is null || requested == snapshot.Current)
        {
            return;
        }

        var generation = _attachmentGeneration;
        _busy = true;
        _resolution.IsEnabled = _refresh.IsEnabled = false;
        string? detail = null;
        try
        {
            var result = await _apply(snapshot, requested);
            if (!result.Applied)
            {
                detail = DisplayText.Mode(result);
            }
        }
        catch (Exception ex)
        {
            detail = "Display change was not confirmed: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }

        if (_closed || generation != _attachmentGeneration)
        {
            if (_readPending && !_closed)
            {
                _readPending = false;
                await ReadAsync();
            }

            return;
        }

        _snapshot = null;
        await ReadAsync();
        if (!_closed && generation == _attachmentGeneration && detail is not null)
        {
            _status.Text += "\n" + detail;
        }
    }
}
