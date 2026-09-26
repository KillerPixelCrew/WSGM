using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Shell;

/// <summary>What the capability hook publishes: the bits to clear and the controller to clear them on.</summary>
/// <param name="Mask">The capability bits to clear, as a decimal string because they exceed 32 bits.</param>
/// <param name="VendorId">The USB vendor id of the controller the mask applies to.</param>
/// <param name="ProductId">The USB product id of the controller the mask applies to.</param>
internal sealed record WsgmControllerCapsState(string Mask, int VendorId, int ProductId);

/// <summary>
///     The Steam UI hook that clears capability bits on WSGM's virtual controller as Steam's pages
///     read the controller list, so they stop offering controls the handheld does not have.
/// </summary>
/// <remarks>
///     <para>
///         Steam reports capabilities per controller type, and its Steam Deck controller always
///         carries trackpads and touch-sensing sticks. The glyph stylesheet hides what it can anchor
///         on a glyph; the configurator's quick settings ("right trackpad behavior", its sensitivity
///         and inversion) are labelled fields with nothing to anchor, read off the reference Claw on
///         2026-09-26, and each client build adds more such lists. Every store takes the list from
///         one generated RPC namespace and converts each entry's <c>capabilities</c>, so the hook
///         wraps that one function and clears the bits the active glyph profile says are absent.
///     </para>
///     <para>
///         Only bits that name a pair the profile marks wholly absent are cleared: Steam cannot express
///         one trackpad or one touch stick, and a device with one still needs the pages. The back
///         button bits are left alone, because which of Steam's two grip bits names L5/R5 has not been
///         confirmed on a device. The mask changes what this UI process believes about the controller
///         and nothing about the native side or the layouts it writes.
///     </para>
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
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        PatchId,
        "wsgmControllerCaps",
        "wsgm-controller-caps-v1:steaminputmanager-getcontrollerlist",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_controller_caps_probe_")}}
            return JSON.stringify({
              service:count(["SteamInputManager.GetControllerList#1","GetControllerListHandler"])
            });
          {{SteamUiProbeJs.Close}}
          """,
        root => SteamUiPatchEvaluation.IsOne(root, "service"),
        "status.installed&&status.hooked&&status.subscribed",
        "!status.installed&&!status.hooked",
        "Controller capability hook");

    /// <summary>Declares the hook and its state.</summary>
    /// <param name="enabled">Whether the hook may be installed and published.</param>
    /// <param name="presentation">The active glyph presentation, whose absent controls decide the mask.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The module.</returns>
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

    /// <summary>The state for one presentation: an empty mask when nothing is absent or no profile is active.</summary>
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

/// <summary>The hook state on the wire, camelCase as the fragment reads it.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsgmControllerCapsState))]
internal sealed partial class WsgmControllerCapsJsonContext : JsonSerializerContext;
