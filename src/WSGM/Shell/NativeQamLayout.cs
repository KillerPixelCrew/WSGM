using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>WSGM's layout of the rows it adds to Steam's Quick Access tabs.</summary>
/// <remarks>
///     Pure data, published through <see cref="SteamQuickAccessLayoutSurface" />. Each section's id is
///     its English title, which is also the id its fold is kept under in
///     <see cref="WSGM.Core.QuickAccessFolds" />, so a user's open sections carry over unchanged. The
///     Reset section has no heading and Profile scope does not fold, as Valve's own rows in them need.
/// </remarks>
internal static class NativeQamLayout
{
    /// <summary>What a value the running game's own profile supplies leads its description with.</summary>
    internal const string AccentLabel = "Game override";

    /// <summary>The layout WSGM publishes.</summary>
    internal static SteamQuickAccessLayout Layout { get; } = new(
        [
            new SteamQuickAccessSection("Profile scope", "Profile scope", "profile", false, ["valveProfileHeader"]),
            new SteamQuickAccessSection("Power profiles", "Power profiles", "sliders", true,
                ["powerPreset", "powerProfile", "hybridCores", "cpuBoost"]),
            new SteamQuickAccessSection("Display and frame rate", "Display and frame rate", "timer", true,
                ["valveOverlayLevel", "frameLimit", "vrr"]),
            new SteamQuickAccessSection("Power limits", "Power limits", "gauge", true, ["powerLimit", "autoTdp"]),
            new SteamQuickAccessSection("Controller", "Controller", "controller", true, ["controllerTarget"])
        ],
        [new SteamQuickAccessSection("Reset", string.Empty, "reset", false, ["valveReset"])],
        [
            new SteamQuickAccessSection("Display", "Display", "display", true, ["resolution", "valveRefreshRate"]),
            new SteamQuickAccessSection("Audio", "Audio", "audio", true, ["audioFormat"])
        ],
        [
            new SteamQuickAccessSection("Charging", "Charging", "batteryCharging", true, ["charging"]),
            new SteamQuickAccessSection("RGB lighting", "RGB lighting", "colors", true, ["lighting"])
        ],
        true,
        AccentLabel);

    /// <summary>A marked description, as the Quick Access rows draw one: the accent label, then the text.</summary>
    /// <param name="description">The row's own description, or null or empty for none.</param>
    /// <returns>The description to publish beside <c>Accent = true</c>.</returns>
    internal static string AccentDescription(string? description)
    {
        return string.IsNullOrEmpty(description) ? AccentLabel : $"{AccentLabel} · {description}";
    }
}
