using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>
///     Makes Steam's Settings &gt; Customization page the hub for WSGM's themes, UI sounds and boot animation.
/// </summary>
/// <remarks>
///     The script is the <c>wsgmCustomization</c> gate in <c>Core\SteamUiAssets\Source\customization.ts</c>. It
///     adds three native sections in front of Valve's Startup Movie section through a transform on the toolkit's
///     shared <c>useMemo</c> claim, beside the toolkit's screensaver gate, and leaves Valve's own sections as Steam
///     draws them. It declares no state and no command: its sections read the publications of
///     <see cref="SteamThemesSurface" />, <see cref="SteamSoundsSurface" /> and <see cref="SteamAnimationsSurface" />
///     and send those pages' commands, so their services stay the only authority.
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
        "customization-hub-v1:use-memo+settings-pages",
        "(()=>{try{return JSON.stringify({ok:true,runtime:!!window.webpackChunksteamui});}"
        + "catch(e){return JSON.stringify({ok:false,error:String(e)});}})()",
        root => SteamUiPatchEvaluation.Flag(root, "ok") && SteamUiPatchEvaluation.Flag(root, "runtime"),
        "status.installed&&status.registered",
        "!status.installed&&!status.registered",
        "Customization hub");

    /// <summary>Declares the hub's gate.</summary>
    /// <returns>The module.</returns>
    public static ISteamUiModule Module()
    {
        return new SteamUiModule("customization", [Patch]);
    }
}
