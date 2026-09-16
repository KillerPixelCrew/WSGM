using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Projects the session brightness owner without a second hardware poll or write path.</summary>
internal sealed class DisplayBrightnessView : Border
{
    private readonly StackPanel _body = new() { Spacing = 4 };
    private readonly CancellationTokenSource _closed = new();
    private readonly NativeQamBrightnessService _service;

    private readonly Slider _slider = new()
        { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };

    private readonly TextBlock _status = new();
    private int? _pending;
    private bool _synchronizing;
    private bool _writing;

    internal DisplayBrightnessView(NativeQamBrightnessService service)
    {
        _service = service;
        Classes.Add("tile");
        Child = _body;
        _body.Children.Add(new TextBlock { Text = "Display brightness", Classes = { "setting-title" } });
        _body.Children.Add(_status);
        _body.Children.Add(_slider);
        _slider.ValueChanged += async (_, _) =>
        {
            if (_synchronizing || _closed.IsCancellationRequested)
            {
                return;
            }

            _pending = (int)Math.Round(_slider.Value);
            await WriteAsync();
        };
        AttachedToVisualTree += async (_, _) =>
        {
            _service.Changed += OnChanged;
            Refresh();
            try
            {
                await _service.ReadAsync();
                Refresh();
            }
            catch (Exception ex)
            {
                _status.Text = "Brightness unavailable: " + ex.Message;
                _slider.IsEnabled = false;
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _service.Changed -= OnChanged;
            _closed.Cancel();
        };
    }

    private void OnChanged()
    {
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        if (_closed.IsCancellationRequested || _writing)
        {
            return;
        }

        var current = _service.Current;
        _synchronizing = true;
        try
        {
            _slider.IsEnabled = current is not null;
            if (current is not null)
            {
                _slider.Value = current.Percent;
            }

            _status.Text = current is null ? "Brightness unavailable for this display" : $"{current.Percent}%";
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private async Task WriteAsync()
    {
        if (_writing)
        {
            return;
        }

        _writing = true;
        string? failure = null;
        try
        {
            while (_pending is { } value && !_closed.IsCancellationRequested)
            {
                _pending = null;
                var result = await _service.SetBrightnessAsync(value, _closed.Token);
                if (result.Succeeded)
                {
                    continue;
                }

                failure = "Brightness change was not confirmed.";
                _pending = null;
                break;
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            _pending = null;
        }
        finally
        {
            _writing = false;
            Refresh();
            if (failure is not null && !_closed.IsCancellationRequested)
            {
                _status.Text = failure;
            }
        }
    }
}
