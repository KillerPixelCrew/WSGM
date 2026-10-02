using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The session services the quick access sheet attaches to each time it opens.</summary>
/// <param name="Device">The device integration projection, when device integration runs.</param>
/// <param name="Performance">The shared RTSS performance rows.</param>
/// <param name="CommonPlugins">Widgets and panels of the explicitly enabled common plugins.</param>
/// <param name="DevicePrerequisites">What device integration still needs before it can run.</param>
/// <param name="Brightness">The panel backlight service.</param>
/// <param name="ManualTdp">The coordinator that owns the manual power mode.</param>
/// <param name="GameLibrary">The Game Library, outside overlay-test.</param>
/// <param name="Themes">The Steam themes, outside overlay-test.</param>
/// <param name="Animations">The boot movie, outside overlay-test.</param>
/// <param name="Graphics">The graphics packages' controls, or their simulation in overlay-test.</param>
/// <param name="Sounds">Steam UI sound packs, outside overlay-test.</param>
/// <param name="Artwork">The shared artwork owner; each browser has its own transient context.</param>
internal sealed record OverlaySources(
    IDeviceOverlaySource? Device = null,
    PerformanceOverlayBridge? Performance = null,
    CommonPluginOverlaySource? CommonPlugins = null,
    DevicePrerequisiteSource? DevicePrerequisites = null,
    NativeQamBrightnessService? Brightness = null,
    DeviceCoordinator? ManualTdp = null,
    GameLibraryService? GameLibrary = null,
    ThemeService? Themes = null,
    AnimationService? Animations = null,
    IGraphicsOverlaySource? Graphics = null,
    SoundPackService? Sounds = null,
    SteamArtworkBrowserSource? Artwork = null);
