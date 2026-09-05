using WSGM.Controls;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Input;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.UiTests;

internal sealed class FakeDevice : IDeviceOverlaySource
{
    private Action? _changed;
    internal int Subscribers { get; private set; }
    public event Action? Changed
    {
        add { _changed += value; Subscribers++; }
        remove { _changed -= value; Subscribers--; }
    }
    public event Action<CanonicalControllerSample>? PhysicalSampleReceived
    {
        add => throw new InvalidOperationException("Unexpected physical input subscription");
        remove => throw new InvalidOperationException("Unexpected physical input subscription removal");
    }
    internal DeviceOverlaySnapshot State { get; set; } = new(true, "Fixture handheld", "Ready", null,
        [new("fixture.temperature", null, DeviceOverlaySection.Overview, DescriptorStatus.Available,
            "Processor temperature", "Synthetic sensor", "45 °C", false)]);
    public DeviceOverlaySnapshot Snapshot() => State;
    internal void Notify() => _changed?.Invoke();
    public PhysicalGlyphRenderPlan? NavigationHint(GlyphControlId control) => null;
    public IDisposable ObservePhysicalSamples() => throw new InvalidOperationException("Unexpected physical input");
    public Task InvokeAsync(DeviceOverlayCapability capability, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected device write");
    public Task CyclePhysicalGlyphSelectionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected glyph write");
    public Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected AutoTDP write");
    public Task CycleControllerTargetAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected controller write");
    public Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected device retry");
    public Task CycleHardwareProfileAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected hardware profile write");
    public Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected authored profile write");
    public void Dispose() => Assert.Equal(0, Subscribers);
}
