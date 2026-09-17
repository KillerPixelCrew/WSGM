using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Core;

/// <summary>
///     The active handheld glyph profile, resolved into what Steam needs.
/// </summary>
/// <remarks>
///     Holds the plugin's profile, never WSGM artwork. <see cref="SteamGlyphCss" /> turns whatever is
///     here into a stylesheet; this type owns only the resolution from a plugin package to Valve
///     resource names and data URIs.
/// </remarks>
internal sealed class SteamInputGlyphDeliveryState
{
    private SteamInputGlyphPresentation? _presentation;

    /// <summary>The resolved artwork and control availability, or null without an active profile.</summary>
    internal SteamInputGlyphPresentation? Current => Volatile.Read(ref _presentation);

    /// <summary>Replaces the active profile.</summary>
    /// <param name="profile">The plugin's imported profile, or null for native presentation.</param>
    /// <param name="nativeArtwork">Keeps control availability while omitting artwork overrides.</param>
    internal void Update(ImportedGlyphProfile? profile, bool nativeArtwork = false)
    {
        var presentation = SteamInputGlyphPresentation.Create(profile);
        Volatile.Write(ref _presentation, nativeArtwork && presentation is not null
            ? presentation with { StableResources = [], ControllerImages = [] }
            : presentation);
    }
}

internal sealed record SteamInputGlyphAssetReference(string DataUri);

internal sealed record SteamInputGlyphResourceMapping(
    string ValvePath,
    GlyphControlId Control,
    SteamInputGlyphAssetReference Asset);

internal sealed record SteamInputGlyphControllerImageMapping(
    string Slot,
    SteamInputGlyphAssetReference Asset);

/// <summary>The overlay lit on the controller diagram while one physical control is selected.</summary>
/// <param name="Control">The physical control.</param>
/// <param name="Asset">Its highlight artwork, in the full-controller image's coordinate space.</param>
internal sealed record SteamInputGlyphHighlightMapping(
    GlyphControlId Control,
    SteamInputGlyphAssetReference Asset);

internal sealed record SteamInputGlyphPresentation(
    string ProfileId,
    int Revision,
    IReadOnlyList<SteamInputGlyphResourceMapping> StableResources,
    IReadOnlyList<SteamInputGlyphControllerImageMapping> ControllerImages,
    IReadOnlyList<GlyphControlId> AbsentControls,
    IReadOnlyList<SteamInputGlyphHighlightMapping> Highlights)
{
    /// <summary>
    ///     Valve's glyph resource names, mapped to the physical control each one depicts.
    /// </summary>
    /// <remarks>
    ///     WSGM's half of the contract: the plugin says which artwork belongs to which control, and this
    ///     map says which Valve resources that control is drawn with. Several Valve names share a
    ///     control because Steam picks a different resource per controller family for the same button.
    /// </remarks>
    /// <summary>Valve's soft-pull trigger glyphs, drawn with the control's soft-pull artwork.</summary>
    /// <remarks>
    ///     The gyro picker offers each trigger twice, "L2-Trigger (Druck)" for a partial pull and
    ///     "L2-Trigger" for the full one, and draws them with different glyphs. A profile without
    ///     soft-pull artwork draws its full-pull glyph for both, which is still its own artwork rather
    ///     than Valve's.
    /// </remarks>
    private static readonly (string Path, GlyphControlId Control)[] SoftPullResourceMap =
    [
        ("/steaminputglyphs/sd_l2_half.svg", GlyphControlId.LeftTrigger),
        ("/steaminputglyphs/sd_r2_half.svg", GlyphControlId.RightTrigger)
    ];

    private static readonly (string Path, GlyphControlId Control)[] StableResourceMap =
    [
        ("/steaminputglyphs/shared_color_button_a.svg", GlyphControlId.FaceSouth),
        ("/steaminputglyphs/shared_button_a.svg", GlyphControlId.FaceSouth),
        ("/steaminputglyphs/ps_button_x.svg", GlyphControlId.FaceSouth),
        ("/steaminputglyphs/shared_color_button_b.svg", GlyphControlId.FaceEast),
        ("/steaminputglyphs/shared_button_b.svg", GlyphControlId.FaceEast),
        ("/steaminputglyphs/ps_button_circle.svg", GlyphControlId.FaceEast),
        ("/steaminputglyphs/shared_color_button_x.svg", GlyphControlId.FaceWest),
        ("/steaminputglyphs/shared_button_x.svg", GlyphControlId.FaceWest),
        ("/steaminputglyphs/ps_button_square.svg", GlyphControlId.FaceWest),
        ("/steaminputglyphs/shared_color_button_y.svg", GlyphControlId.FaceNorth),
        ("/steaminputglyphs/shared_button_y.svg", GlyphControlId.FaceNorth),
        ("/steaminputglyphs/ps_button_triangle.svg", GlyphControlId.FaceNorth),
        ("/steaminputglyphs/shared_dpad_up.svg", GlyphControlId.DpadUp),
        ("/steaminputglyphs/shared_dpad_down.svg", GlyphControlId.DpadDown),
        ("/steaminputglyphs/shared_dpad_left.svg", GlyphControlId.DpadLeft),
        ("/steaminputglyphs/shared_dpad_right.svg", GlyphControlId.DpadRight),
        ("/steaminputglyphs/shared_l3.svg", GlyphControlId.LeftStick),
        ("/steaminputglyphs/shared_lstick.svg", GlyphControlId.LeftStick),
        ("/steaminputglyphs/shared_lstick_click.svg", GlyphControlId.LeftStick),
        ("/steaminputglyphs/shared_lstick_touch.svg", GlyphControlId.LeftStickTouch),
        ("/steaminputglyphs/shared_r3.svg", GlyphControlId.RightStick),
        ("/steaminputglyphs/shared_rstick.svg", GlyphControlId.RightStick),
        ("/steaminputglyphs/shared_rstick_click.svg", GlyphControlId.RightStick),
        ("/steaminputglyphs/shared_rstick_touch.svg", GlyphControlId.RightStickTouch),
        ("/steaminputglyphs/shared_l1.svg", GlyphControlId.LeftShoulder),
        ("/steaminputglyphs/shared_r1.svg", GlyphControlId.RightShoulder),
        ("/steaminputglyphs/shared_l2.svg", GlyphControlId.LeftTrigger),
        ("/steaminputglyphs/shared_r2.svg", GlyphControlId.RightTrigger),

        // The sd_* family, which is what the page actually draws while WSGM presents a Steam Deck
        // virtual pad — read off the live Steam Input page on the reference Claw, where the German
        // row labels name each one: sd_l1 "Linke Schultertaste", sd_r1 "Rechte Schultertaste".
        // Without these the shoulders, triggers and rear paddles kept Valve's artwork while every
        // face button and d-pad glyph was correctly replaced.
        ("/steaminputglyphs/sd_l1.svg", GlyphControlId.LeftShoulder),
        ("/steaminputglyphs/sd_r1.svg", GlyphControlId.RightShoulder),
        ("/steaminputglyphs/sd_l2.svg", GlyphControlId.LeftTrigger),
        ("/steaminputglyphs/sd_r2.svg", GlyphControlId.RightTrigger),

        // The Deck's rear pairs. M1 is the LEFT paddle and M2 the RIGHT one — measured on the
        // reference unit and recorded in the plugin's own notes, which explicitly correct
        // Handheld Companion for having them inverted. The second pair (l5/r5) has no counterpart
        // on this device and is declared absent by the profile instead.
        ("/steaminputglyphs/sd_l4.svg", GlyphControlId.RearM1),
        ("/steaminputglyphs/sd_r4.svg", GlyphControlId.RearM2),
        ("/steaminputglyphs/sd_l5.svg", GlyphControlId.RearLeft2),
        ("/steaminputglyphs/sd_r5.svg", GlyphControlId.RearRight2),
        ("/steaminputglyphs/xbox_button_logo.svg", GlyphControlId.Guide),
        ("/steaminputglyphs/ps4_button_logo.svg", GlyphControlId.Guide),
        ("/steaminputglyphs/sc_button_steam.svg", GlyphControlId.Guide),
        ("/steaminputglyphs/xbox_button_select.svg", GlyphControlId.View),
        ("/steaminputglyphs/xbox360_button_select.svg", GlyphControlId.View),
        ("/steaminputglyphs/ps5_button_create.svg", GlyphControlId.View),
        ("/steaminputglyphs/sd_button_view.svg", GlyphControlId.View),
        ("/steaminputglyphs/xbox_button_start.svg", GlyphControlId.Menu),
        ("/steaminputglyphs/xbox360_button_start.svg", GlyphControlId.Menu),
        ("/steaminputglyphs/ps5_button_options.svg", GlyphControlId.Menu),
        ("/steaminputglyphs/sd_button_menu.svg", GlyphControlId.Menu),
        ("/steaminputglyphs/qam_icon.svg", GlyphControlId.QuickAccess),
        ("/steaminputglyphs/shared_m1.svg", GlyphControlId.RearM1),
        ("/steaminputglyphs/shared_m2.svg", GlyphControlId.RearM2)
    ];

    /// <summary>Resolves an imported plugin profile into Valve resource mappings.</summary>
    /// <param name="profile">The plugin's imported glyph profile.</param>
    /// <returns>The resolved presentation, or null when there is nothing to present.</returns>
    internal static SteamInputGlyphPresentation? Create(ImportedGlyphProfile? profile)
    {
        if (profile is null || profile.Manifest.ProfileId.Length == 0)
        {
            return null;
        }

        var controls = profile.Manifest.Controls
            .ToDictionary(mapping => mapping.Control);
        var aliases = profile.Manifest.Aliases
            .ToDictionary(mapping => mapping.LogicalControl, mapping => mapping.PhysicalControl);
        Dictionary<string, SteamInputGlyphAssetReference> assetReferences =
            new(StringComparer.Ordinal);
        List<SteamInputGlyphResourceMapping> resources = [];
        foreach (var (path, logicalControl) in StableResourceMap)
        {
            var physicalControl = aliases.GetValueOrDefault(logicalControl, logicalControl);
            if (!controls.TryGetValue(physicalControl, out var mapping)
                || mapping.Presence is not GlyphControlPresence.Present
                || mapping.AssetId is not { Length: > 0 } assetId
                || !TryGetAsset(
                    profile,
                    assetReferences,
                    assetId,
                    out var asset))
            {
                continue;
            }

            resources.Add(new SteamInputGlyphResourceMapping(path, logicalControl, asset));
        }

        foreach (var (path, logicalControl) in SoftPullResourceMap)
        {
            var physicalControl = aliases.GetValueOrDefault(logicalControl, logicalControl);
            if (!controls.TryGetValue(physicalControl, out var mapping)
                || mapping.Presence is not GlyphControlPresence.Present
                || (mapping.SoftPullAssetId ?? mapping.AssetId) is not { Length: > 0 } assetId
                || !TryGetAsset(
                    profile,
                    assetReferences,
                    assetId,
                    out var asset))
            {
                continue;
            }

            resources.Add(new SteamInputGlyphResourceMapping(path, logicalControl, asset));
        }

        List<SteamInputGlyphControllerImageMapping> images = [];
        AddControllerImage(
            profile,
            assetReferences,
            images,
            "full",
            profile.Manifest.ControllerImages.FullAssetId);
        AddControllerImage(
            profile,
            assetReferences,
            images,
            "left",
            profile.Manifest.ControllerImages.LeftAssetId);
        AddControllerImage(
            profile,
            assetReferences,
            images,
            "right",
            profile.Manifest.ControllerImages.RightAssetId);

        // Highlights are keyed by the physical control, because the diagram's regions are physical
        // positions: an alias changes which artwork a logical control borrows, not where it sits.
        List<SteamInputGlyphHighlightMapping> highlights = [];
        foreach (var mapping in profile.Manifest.Controls)
        {
            if (mapping.Presence is GlyphControlPresence.Present
                && mapping.HighlightAssetId is { Length: > 0 } highlightId
                && TryGetAsset(profile, assetReferences, highlightId, out var highlight))
            {
                highlights.Add(new SteamInputGlyphHighlightMapping(mapping.Control, highlight));
            }
        }

        // Absence is the default. The plugin declares the controls its device HAS, and everything
        // it does not name is hidden — so a handheld with no trackpads gets no trackpad sections
        // without having to say so, and a profile cannot leave a section behind by forgetting to
        // list it. Declaring absence explicitly was the previous model and it fails the same way
        // every allowlist-by-omission does: the entry nobody remembered to add is the one that
        // shows up on screen.
        var present = profile.Manifest.Controls
            .Where(mapping => mapping.Presence is GlyphControlPresence.Present)
            .Select(mapping => mapping.Control)
            .ToHashSet();
        foreach (var alias in profile.Manifest.Aliases)
        {
            if (present.Contains(alias.PhysicalControl))
            {
                present.Add(alias.LogicalControl);
            }
        }

        var absent = Enum.GetValues<GlyphControlId>()
            .Where(control => !present.Contains(control))
            .OrderBy(control => control)
            .ToArray();

        return new SteamInputGlyphPresentation(
            profile.Manifest.ProfileId,
            profile.Manifest.Revision,
            resources,
            images,
            absent,
            highlights);
    }

    private static void AddControllerImage(
        ImportedGlyphProfile profile,
        IDictionary<string, SteamInputGlyphAssetReference> assetReferences,
        List<SteamInputGlyphControllerImageMapping> images,
        string slot,
        string? assetId)
    {
        if (assetId is { Length: > 0 }
            && TryGetAsset(
                profile,
                assetReferences,
                assetId,
                out var asset))
        {
            images.Add(new SteamInputGlyphControllerImageMapping(slot, asset));
        }
    }

    private static bool TryGetAsset(
        ImportedGlyphProfile profile,
        IDictionary<string, SteamInputGlyphAssetReference> assetReferences,
        string assetId,
        out SteamInputGlyphAssetReference reference)
    {
        reference = null!;
        if (assetReferences.TryGetValue(
                assetId,
                out var existing))
        {
            reference = existing;
            return true;
        }

        if (!profile.Assets.TryGetValue(assetId, out var asset))
        {
            return false;
        }

        string mediaType;
        ReadOnlySpan<byte> bytes;
        switch (asset.Entry.Format)
        {
            case GlyphAssetFormat.Svg when asset.Vector is not null:
                mediaType = "image/svg+xml";
                bytes = asset.Vector.SvgUtf8.Span;
                break;
            case GlyphAssetFormat.Png when !asset.RasterPng.IsEmpty:
                mediaType = "image/png";
                bytes = asset.RasterPng.Span;
                break;
            default:
                return false;
        }

        reference = new SteamInputGlyphAssetReference(
            $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}");
        assetReferences.Add(assetId, reference);
        return true;
    }
}
