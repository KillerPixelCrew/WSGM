using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>WSGM's settings page inside Steam, opened from WSGM's row in Steam's main menu.</summary>
internal static class SteamWsgmSettingsSurface
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "steam-ui.wsgm-settings";

    /// <summary>The route this page is served at; each sidebar page sits below it.</summary>
    public const string Route = "/wsgm/settings";

    /// <summary>The renderer that draws it.</summary>
    public const string Template = "wsgm-settings";

    /// <summary>The longest key the page publishes, with room to spare.</summary>
    /// <remarks>
    ///     A plugin enable key carries a plugin id and an instance id of up to 128 characters each
    ///     behind its prefix, 273 in all; a smaller bound would refuse that toggle on every press.
    /// </remarks>
    internal const int MaximumKeyLength = 320;

    /// <summary>The exact command vocabulary the page emits.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["set"];

    /// <summary>Installs the settings renderer and its state subscription.</summary>
    /// <remarks>
    ///     Every component the toolkit's settings renderer draws with is required, each counted
    ///     separately, so an incompatible client says which one moved: the page is Steam's own
    ///     Settings layout or nothing, never an imitation of it.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        PatchId,
        "wsgmSettings",
        "steam-wsgm-settings-v1:native-settings-components",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_wsgm_settings_probe_")}}
            return JSON.stringify({
              react:count({{SteamUiProbeJs.ReactTokens}}),
              focusable:count({{SteamUiProbeJs.NativeFocusableTokens}}),
              controls:count({{SteamUiProbeJs.NativeFieldTokens}}),
              showModal:count({{SteamUiProbeJs.NativeShowModalTokens}}),
              pages:count(['disableRouteReporting']),
              confirm:count(['strMiddleButtonText','bProgressDialog','bAlertDialog'])
            });
          {{SteamUiProbeJs.Close}}
          """,
        root => SteamUiPatchEvaluation.IsOne(root, "react")
                && SteamUiPatchEvaluation.IsOne(root, "focusable")
                && SteamUiPatchEvaluation.IsOne(root, "controls")
                && SteamUiPatchEvaluation.IsOne(root, "showModal")
                && SteamUiPatchEvaluation.IsOne(root, "pages")
                && SteamUiPatchEvaluation.IsOne(root, "confirm"),
        "status.installed&&status.resolved&&status.subscribed",
        "!status.installed",
        "WSGM settings");

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether the page may be installed and published.</param>
    /// <param name="read">Reads the current page model.</param>
    /// <param name="backend">Answers changes.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The module.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<WsgmSteamSettingsState?>> read,
        IWsgmSteamSettingsBackend backend,
        string id = "wsgm-settings")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId, enabled, read, WsgmSteamSettingsJsonContext.Default.WsgmSteamSettingsState)
            ],
            [
                SteamUiModuleBuilder.Command<SetRequest>(PatchId, "set", TryReadSet,
                    (request, token) => backend.SetAsync(request.Key, request.Value, token),
                    "The setting payload is invalid.")
            ]);
    }

    /// <summary>Reads <c>{key, value}</c>: a bounded key and a value the backend checks against the row.</summary>
    internal static bool TryReadSet(JsonElement payload, out SetRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadBoundedString(payload, "key", MaximumKeyLength, out var key)
            || !payload.TryGetProperty("value", out var setting)
            || setting.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Null)
        {
            return false;
        }

        value = new SetRequest(key, setting.Clone());
        return true;
    }

    /// <summary>One change: the row's key and its new value.</summary>
    internal readonly record struct SetRequest(string Key, JsonElement Value);
}

/// <summary>The page model on the wire, camelCase as the renderer reads it.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsgmSteamSettingsState))]
internal sealed partial class WsgmSteamSettingsJsonContext : JsonSerializerContext;
