using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>WSGM's settings page inside Steam, opened from WSGM's row in Steam's main menu.</summary>
internal static class SteamWsgmSettingsSurface
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "wsgm.settings";

    /// <summary>The route this page is served at; each sidebar page sits below it.</summary>
    public const string Route = "/wsgm/settings";

    /// <summary>The renderer that draws it.</summary>
    public const string Template = "wsgm-settings";

    /// <summary>The name the page's gate registers under.</summary>
    public const string GateName = "wsgmSettings";

    /// <summary>The exact command vocabulary the page emits.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["set"];

    /// <summary>Installs the settings renderer and its state subscription.</summary>
    /// <remarks>
    ///     Every component the toolkit's settings renderer draws with is required, each counted
    ///     separately, so an incompatible client says which one moved: the page is Steam's own
    ///     Settings layout or nothing, never an imitation of it.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-wsgm-settings-v2:steam-page",
        "WSGM settings",
        [
            SteamPageProbe.React, SteamPageProbe.Focusable, SteamPageProbe.Fields, SteamPageProbe.ShowModal,
            SteamPageProbe.SettingsSidebar, SteamPageProbe.ConfirmModal
        ]);

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether state may be published; patch installation is coordinated separately.</param>
    /// <param name="read">Reads the current model; null skips this publication without retracting the previous state.</param>
    /// <param name="backend">Answers changes.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>A module borrowing its backend and readers; construction does not install its patch.</returns>
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

    /// <summary>Reads <c>{key, value}</c>, retaining a cloned value for backend row validation.</summary>
    /// <param name="payload">An object containing exactly key and value.</param>
    /// <param name="value">Parsed request with a cloned JSON value independent of the payload document; default on failure.</param>
    /// <returns>
    ///     True when the key is nonblank and the value is neither object, null nor undefined; the backend validates
    ///     row-specific types.
    /// </returns>
    internal static bool TryReadSet(JsonElement payload, out SetRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "key", out var key)
            || !payload.TryGetProperty("value", out var setting)
            || setting.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Null)
        {
            return false;
        }

        value = new SetRequest(key, setting.Clone());
        return true;
    }

    /// <summary>One change: the row's key and its new value.</summary>
    /// <param name="Key">Published row identity.</param>
    /// <param name="Value">Cloned new JSON value; row semantics are validated by the backend.</param>
    internal readonly record struct SetRequest(string Key, JsonElement Value);
}

/// <summary>The page model on the wire, camelCase as the renderer reads it.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsgmSteamSettingsState))]
internal sealed partial class WsgmSteamSettingsJsonContext : JsonSerializerContext;
