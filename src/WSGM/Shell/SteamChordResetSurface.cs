using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What the chord reset hook publishes: whether the mirror is watching.</summary>
/// <param name="Active">Whether the guide chord mirror is active, so a reset needs reporting.</param>
internal sealed record WsgmChordMirrorState(bool Active);

/// <summary>
///     The Steam UI hook that reports the layout editor's "reset to defaults" for the guide button
///     chord layout, so <see cref="SteamGuideChordMirror" /> can put Valve's template back before
///     Steam reloads it.
/// </summary>
/// <remarks>
///     The editor resets by selecting <c>default://…/controller_base/chord_neptune.vdf</c> for the
///     chord pseudo-app and reloading three seconds later. The hook wraps
///     <c>SteamClient.Input.SetSelectedConfigForApp</c> in SharedJSContext, where Steam's own
///     configurator store calls it, and sends one <c>reset</c> command when that selection is made for
///     app 443510 while the mirror is active. Nothing else about the call changes; the original runs
///     with the original arguments.
/// </remarks>
internal static class SteamChordResetSurface
{
    /// <summary>Stable id of the hook.</summary>
    public const string PatchId = "wsgm.chord-reset";

    /// <summary>The command the hook may send.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["reset"];

    /// <summary>The gate: installed while the mirror feature is on, whatever the mirror's state.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        PatchId,
        "wsgmChordReset",
        "wsgm-chord-reset-v1:steamclient-input-selection",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_chord_reset_probe_")}}
            return JSON.stringify({
              input:typeof SteamClient?.Input?.SetSelectedConfigForApp==='function'
            });
          {{SteamUiProbeJs.Close}}
          """,
        root => SteamUiPatchEvaluation.Flag(root, "input"),
        "status.installed&&status.hooked&&status.subscribed",
        "!status.installed&&!status.hooked",
        "Guide chord reset hook");

    /// <summary>Declares the hook's state and its one command.</summary>
    /// <param name="enabled">Whether the hook may be installed and published.</param>
    /// <param name="mirror">The mirror the hook reports to.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The module.</returns>
    public static ISteamUiModule Module(Func<bool> enabled, SteamGuideChordMirror mirror, string id = "chord-reset")
    {
        ArgumentNullException.ThrowIfNull(mirror);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId,
                    enabled,
                    () => new ValueTask<WsgmChordMirrorState?>(new WsgmChordMirrorState(mirror.Active)),
                    WsgmChordMirrorJsonContext.Default.WsgmChordMirrorState)
            ],
            [
                SteamUiModuleBuilder.Command<bool>(
                    PatchId,
                    "reset",
                    TryReadReset,
                    (_, _) => Task.FromResult(Reset(mirror)),
                    "The chord reset carries no payload.")
            ]);
    }

    private static SteamUiCommandResult Reset(SteamGuideChordMirror mirror)
    {
        var restored = mirror.RestoreDefault();
        Log.Info(restored
            ? "Guide chord layout reset: Valve's template restored ahead of Steam's reload."
            : "Guide chord layout reset reported with nothing mirrored; Steam's own template stays.");
        return new SteamUiCommandResult(true, null);
    }

    /// <summary>Accepts the empty payload the hook sends and nothing else.</summary>
    internal static bool TryReadReset(JsonElement payload, out bool value)
    {
        value = true;
        return payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
               || (payload.ValueKind is JsonValueKind.Object && SteamUiPayload.HasExactly(payload, 0));
    }
}

/// <summary>The hook state on the wire, camelCase as the fragment reads it.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsgmChordMirrorState))]
internal sealed partial class WsgmChordMirrorJsonContext : JsonSerializerContext;
