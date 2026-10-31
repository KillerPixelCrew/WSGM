using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Projects the session brightness owner without a second hardware poll or write path.</summary>
/// <remarks>
///     The shared slider row settles a drag or a held press before it writes, and its description carries
///     an unavailable display or a refused change.
/// </remarks>
internal sealed class DisplayBrightnessView : Border
{
    private readonly CancellationTokenSource _closed = new();
    private readonly NativeQamBrightnessService _service;
    private readonly DeviceSliderRow _slider;
    private string _failure = string.Empty;
    private int? _pending;
    private int? _requested;
    private bool _writing;

    internal DisplayBrightnessView(NativeQamBrightnessService service)
    {
        _service = service;
        var current = service.Current;
        _slider = new DeviceSliderRow("display.brightness", "Display brightness", string.Empty, 0, 100, 1,
            CapabilityUnit.Percent, current?.Percent, current is not null, value =>
            {
                if (_closed.IsCancellationRequested)
                {
                    return;
                }

                _pending = value;
                _requested = value;
                _failure = string.Empty;
                _slider!.RefreshDescription(string.Empty);
                _ = WriteAsync();
            });
        Child = _slider;
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
                _failure = "Brightness unavailable: " + ex.Message;
                _slider.RefreshReadback(0, 100, 1, null, false);
                _slider.RefreshDescription(_failure);
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
        _slider.RefreshReadback(0, 100, 1, current?.Percent, current is not null);
        if (current is null)
        {
            _slider.RefreshDescription("Brightness unavailable for this display");
        }
        else if (_requested is null || current.Percent == _requested)
        {
            _failure = string.Empty;
            _slider.RefreshDescription(string.Empty);
        }
        else
        {
            _slider.RefreshDescription(_failure);
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

                failure = result.Error ?? "Brightness change was not confirmed.";
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
            if (failure is not null && !_closed.IsCancellationRequested)
            {
                _failure = failure;
            }

            Refresh();
        }
    }
}
