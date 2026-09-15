using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The session services the quick access sheet attaches to each time it opens.</summary>
/// <param name="Device">The device integration projection, when device integration runs.</param>
/// <param name="Performance">The shared RTSS performance rows.</param>
/// <param name="CommonPlugins">Widgets and panels of the explicitly enabled common plugins.</param>
/// <param name="DevicePrerequisites">What device integration still needs before it can run.</param>
/// <param name="Brightness">The panel backlight service.</param>
/// <param name="ManualTdp">The coordinator that owns the manual power mode.</param>
internal sealed record OverlaySources(
    IDeviceOverlaySource? Device = null,
    PerformanceOverlayBridge? Performance = null,
    CommonPluginOverlaySource? CommonPlugins = null,
    DevicePrerequisiteSource? DevicePrerequisites = null,
    NativeQamBrightnessService? Brightness = null,
    DeviceCoordinator? ManualTdp = null);
