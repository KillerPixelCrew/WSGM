using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>
///     Makes Steam's Settings &gt; Customization page the hub for WSGM's themes, UI sounds and boot animation.
/// </summary>
/// <remarks>
///     The script is the <c>wsgmCustomization</c> gate in <c>Core\SteamUiAssets\Source\customization.ts</c>, a
///     toolkit <c>defineGate</c> gate whose probe resolves Steam's settings fields and the Customization route. It
///     adds three native sections in front of Valve's Startup Movie section as one of the toolkit's shared Settings
///     sections (<c>registerSteamSettingsSection</c>), the same claim the toolkit's screensaver gate draws its rows
///     through, and leaves Valve's own sections as Steam draws them. It declares no state and no command: its
///     sections read the publications of <see cref="SteamThemesSurface" />, <see cref="SteamSoundsSurface" /> and
///     <see cref="SteamAnimationsSurface" /> and send those pages' commands, so their services stay the only
///     authority, and a refused change is shown on the row that asked.
/// </remarks>
internal static class SteamCustomizationSurface
{
    /// <summary>The hub's stable patch id.</summary>
    public const string PatchId = "wsgm.customization";

    /// <summary>The name the gate registers under.</summary>
    public const string GateName = "wsgmCustomization";

    /// <summary>Installs the transform while WSGM's host surfaces are on and removes it with them.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        GateName,
        "customization-hub-v2:settings-section",
        "Customization hub");

    /// <summary>Declares the hub's gate.</summary>
    /// <returns>The module.</returns>
    public static ISteamUiModule Module()
    {
        return new SteamUiModule("customization", [Patch]);
    }
}
