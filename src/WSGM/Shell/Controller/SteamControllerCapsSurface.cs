using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Shell;

/// <summary>
///     The Steam UI hook that clears capability bits on WSGM's virtual controller as Steam's pages
///     read the controller list, so they stop offering controls the handheld does not have.
/// </summary>
/// <remarks>
///     Only wholly absent control pairs are masked because Steam exposes paired capability bits.
///     Grip-button bits remain unchanged without a verified mapping. The mask affects UI RPC replies,
///     not native controller state or saved layouts.
/// </remarks>
internal static class SteamControllerCapsSurface
{
    /// <summary>Stable id of the hook.</summary>
    public const string PatchId = "wsgm.controller-caps";

    /// <summary>Valve's vendor id, which the Steam Deck composite target presents.</summary>
    internal const int SteamDeckVendorId = 0x28DE;

    /// <summary>The Steam Deck controller's product id, which the composite target presents.</summary>
    internal const int SteamDeckProductId = 0x1205;

    /// <summary>Steam's <c>ATTRIBCAP_TRACKPAD</c>: both trackpads.</summary>
    internal const ulong TrackpadBit = 1UL << 12;

    /// <summary>Steam's <c>ATTRIBCAP_CAPJOYSTICK</c>: both sticks sense touch.</summary>
    internal const ulong CapacitiveStickBit = 1UL << 24;

    /// <summary>The gate: installed with the host Steam UI; the state says what, if anything, to clear.</summary>
    /// <remarks>Its probe is the gate's own: the controller-list service module must match exactly once.</remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "wsgmControllerCaps",
        "wsgm-controller-caps-v1:steaminputmanager-getcontrollerlist",
        "Controller capability hook");

    /// <summary>Declares the hook and its state.</summary>
    /// <param name="enabled">Whether hook state may be published; patch installation is coordinated separately.</param>
    /// <param name="presentation">The active glyph presentation, whose absent controls decide the mask.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>A module borrowing its backend and readers; construction does not install its patch.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<SteamInputGlyphPresentation?> presentation,
        string id = "controller-caps")
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId,
                    enabled,
                    () => new ValueTask<WsgmControllerCapsState?>(State(presentation())),
                    WsgmControllerCapsJsonContext.Default.WsgmControllerCapsState)
            ]);
    }

    /// <summary>The state for one presentation: a zero mask when nothing is absent or no profile is active.</summary>
    /// <param name="presentation">The active glyph presentation, or null.</param>
    /// <returns>The state.</returns>
    internal static WsgmControllerCapsState State(SteamInputGlyphPresentation? presentation)
    {
        return new WsgmControllerCapsState(Mask(presentation).ToString(), SteamDeckVendorId, SteamDeckProductId);
    }

    /// <summary>The capability bits the presentation's absent controls clear.</summary>
    /// <param name="presentation">The active glyph presentation, or null.</param>
    /// <returns>The mask, zero when nothing is cleared.</returns>
    internal static ulong Mask(SteamInputGlyphPresentation? presentation)
    {
        if (presentation is null)
        {
            return 0;
        }

        HashSet<GlyphControlId> absent = [.. presentation.AbsentControls];
        ulong mask = 0;
        if (absent.Contains(GlyphControlId.LeftTrackpad) && absent.Contains(GlyphControlId.RightTrackpad))
        {
            mask |= TrackpadBit;
        }

        if (absent.Contains(GlyphControlId.LeftStickTouch) && absent.Contains(GlyphControlId.RightStickTouch))
        {
            mask |= CapacitiveStickBit;
        }

        return mask;
    }
}
