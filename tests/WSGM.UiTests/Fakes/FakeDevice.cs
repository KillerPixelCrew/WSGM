using LibHandheld.Contracts;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.UiTests.Fakes;

internal sealed class FakeDevice : IDeviceOverlaySource
{
    private Action? _changed;
    internal IDeviceOverlaySource? SampleSource { get; set; }
    internal int Subscribers { get; private set; }

    internal DeviceOverlaySnapshot State { get; set; } = new(true, "Fixture handheld", "Ready", null,
    [
        new DeviceOverlayCapability("fixture.temperature", null, DeviceOverlaySection.Overview,
            DescriptorStatus.Available,
            "Processor temperature", "Synthetic sensor", "45 °C", false)
    ]);

    internal Func<DeviceOverlayCapability, CancellationToken, Task>? Invoke { get; set; }
    internal Func<DeviceGlyphSelection, Task>? SelectGlyphs { get; set; }

    public event Action? Changed
    {
        add
        {
            _changed += value;
            Subscribers++;
        }
        remove
        {
            _changed -= value;
            Subscribers--;
        }
    }

    public DeviceOverlaySnapshot Snapshot()
    {
        return State;
    }

    public PhysicalGlyphRenderPlan? NavigationHint(GlyphControlId control)
    {
        return null;
    }

    public IDisposable ObservePhysicalSamples()
    {
        return SampleSource?.ObservePhysicalSamples() ??
               throw new InvalidOperationException("Unexpected physical input");
    }

    public Task InvokeAsync(DeviceOverlayCapability capability, CancellationToken cancellationToken = default)
    {
        return Invoke?.Invoke(capability, cancellationToken) ??
               throw new InvalidOperationException("Unexpected device write");
    }

    public Task SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection selection,
        CancellationToken cancellationToken = default)
    {
        return SelectGlyphs?.Invoke(selection) ?? throw new InvalidOperationException("Unexpected glyph write");
    }

    public Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Unexpected AutoTDP write");
    }

    public Task SetHostSelectionAsync(string rowId, string? value, CancellationToken cancellationToken = default)
    {
        return Task.FromException(new NotSupportedException("Explicit host selection is not available."));
    }

    public Task CycleControllerTargetAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Unexpected controller write");
    }

    public Task UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Unexpected device retry");
    }

    public Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Unexpected authored profile write");
    }

    public void Dispose()
    {
        Assert.Equal(0, Subscribers);
    }

    public event Action<CanonicalControllerSample>? PhysicalSampleReceived
    {
        add
        {
            if (SampleSource is not null)
            {
                SampleSource.PhysicalSampleReceived += value;
            }
            else
            {
                throw new InvalidOperationException("Unexpected physical input subscription");
            }
        }
        remove
        {
            if (SampleSource is not null)
            {
                SampleSource.PhysicalSampleReceived -= value;
            }
            else
            {
                throw new InvalidOperationException("Unexpected physical input subscription removal");
            }
        }
    }

    internal void Notify()
    {
        _changed?.Invoke();
    }
}
