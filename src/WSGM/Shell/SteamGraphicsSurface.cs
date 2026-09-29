using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>What answers the Graphics page in Steam.</summary>
internal interface ISteamGraphicsBackend
{
    /// <summary>Writes one graphics setting.</summary>
    /// <param name="key">The row's key, as published.</param>
    /// <param name="value">The new value, in the row's own shape.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the plugin took it, and why not when it did not.</returns>
    Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken);
}

/// <summary>What the Graphics page in Steam draws.</summary>
/// <param name="Pages">The sidebar's pages, one per adapter and display.</param>
/// <param name="Revision">Increases whenever what the page shows may have changed.</param>
internal sealed record SteamGraphicsState(IReadOnlyList<SteamSettingsPage> Pages, long Revision);

/// <summary>
///     The Graphics page inside Steam, opened from its row in Steam's main menu while a graphics package
///     runs.
/// </summary>
internal static class SteamGraphicsSurface
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "steam-ui.wsgm-graphics";

    /// <summary>The route this page is served at; each sidebar page sits below it.</summary>
    public const string Route = "/wsgm/graphics";

    /// <summary>The renderer that draws it.</summary>
    public const string Template = "wsgm-graphics";

    /// <summary>The name the page's gate registers under.</summary>
    public const string GateName = "wsgmGraphics";

    /// <summary>The exact command vocabulary the page emits.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["set"];

    /// <summary>Installs the Graphics renderer and its state subscription.</summary>
    /// <remarks>
    ///     The same components as WSGM's settings page, because the page is drawn by the same renderer:
    ///     Steam's own Settings layout or nothing.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-wsgm-graphics-v1:steam-page",
        "Graphics",
        [
            SteamPageProbe.React, SteamPageProbe.Focusable, SteamPageProbe.Fields, SteamPageProbe.ShowModal,
            SteamPageProbe.SettingsSidebar, SteamPageProbe.ConfirmModal
        ]);

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether the page may be installed and published.</param>
    /// <param name="read">Reads the current page model.</param>
    /// <param name="backend">Answers changes.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The module.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamGraphicsState?>> read,
        ISteamGraphicsBackend backend,
        string id = "wsgm-graphics")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId, enabled, read, SteamGraphicsJsonContext.Default.SteamGraphicsState)
            ],
            [
                SteamUiModuleBuilder.Command<SetRequest>(PatchId, "set", TryReadSet,
                    (request, token) => backend.SetAsync(request.Key, request.Value, token),
                    "The graphics setting payload is invalid.")
            ]);
    }

    /// <summary>Reads <c>{key, value}</c>: a key and a value the backend checks against the row.</summary>
    internal static bool TryReadSet(JsonElement payload, out SetRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "key", out var key)
            || !payload.TryGetProperty("value", out var setting)
            || setting.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number
                or JsonValueKind.String))
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
[JsonSerializable(typeof(SteamGraphicsState))]
internal sealed partial class SteamGraphicsJsonContext : JsonSerializerContext;
