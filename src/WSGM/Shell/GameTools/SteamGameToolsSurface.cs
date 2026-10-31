using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The routed Big Picture Game Tools page; every command carries the AppID it acts on.</summary>
internal static class SteamGameToolsSurface
{
    /// <summary>The state and command namespace.</summary>
    internal const string PatchId = "wsgm.game-tools";

    /// <summary>The name the page's gate registers under.</summary>
    internal const string GateName = "gameTools";

    /// <summary>The Steam router pattern registered by this surface.</summary>
    internal const string Route = "/wsgm/game-tools/:appid";

    /// <summary>The page template the frontend registers.</summary>
    internal const string Template = "game-tools";

    /// <summary>The page id in the host's page list.</summary>
    internal const string PageId = "game-tools";

    /// <summary>The page title.</summary>
    internal const string Title = "Game Tools";

    /// <summary>Installs the page renderer and its state subscription.</summary>
    internal static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId, GateName, "wsgm-game-tools-v1:steam-page", Title);

    /// <summary>The concrete route that opens the page for one game.</summary>
    /// <param name="appId">The game.</param>
    /// <returns>The path Steam's router navigates to.</returns>
    internal static string RouteFor(uint appId)
    {
        return "/wsgm/game-tools/" + appId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Declares the page state and its exact commands.</summary>
    /// <param name="enabled">Whether state may be published.</param>
    /// <param name="service">The shared Game Tools owner.</param>
    /// <returns>A module borrowing the service.</returns>
    internal static ISteamUiModule Module(Func<bool> enabled, GameToolsService service)
    {
        return new SteamUiModule(Template, [Patch],
            [
                SteamUiModuleBuilder.Publication(PatchId, enabled, service.ReadStateAsync,
                    GameToolsJsonContext.Default.GameToolsState)
            ],
            [
                SteamUiModuleBuilder.Command<uint>(PatchId, "open", TryAppId, service.OpenAsync,
                    "A valid game AppID is required."),
                SteamUiModuleBuilder.Command<uint>(PatchId, "refresh", TryAppId, service.RefreshAsync,
                    "A valid game AppID is required."),
                SteamUiModuleBuilder.Command<uint>(PatchId, "checkUpdates", TryAppId, service.CheckUpdatesAsync,
                    "A valid game AppID is required."),
                SteamUiModuleBuilder.Command<uint>(PatchId, "cancel", TryAppId, service.CancelAsync,
                    "A valid game AppID is required."),
                SteamUiModuleBuilder.Command<(uint AppId, string Path)>(PatchId, "chooseRuntime", TryRuntime,
                    (value, token) => service.ChooseRuntimeAsync(value.AppId, value.Path, token),
                    "Choose the game's program."),
                SteamUiModuleBuilder.Command<GameToolsInstallRequest>(PatchId, "install", TryInstall,
                    service.InstallAsync, "The install request is invalid."),
                SteamUiModuleBuilder.Command<(uint AppId, string ToolId, bool ReplaceConfig)>(PatchId, "update",
                    TryUpdate, (value, token) => service.UpdateAsync(value.AppId, value.ToolId, value.ReplaceConfig, token),
                    "The update request is invalid."),
                SteamUiModuleBuilder.Command<(uint AppId, string ToolId)>(PatchId, "repair", TryTool,
                    (value, token) => service.RepairAsync(value.AppId, value.ToolId, token),
                    "The repair request is invalid."),
                SteamUiModuleBuilder.Command<(uint AppId, string ToolId, string? ProxyName,
                    IReadOnlyDictionary<string, string> Settings)>(PatchId, "configure", TryConfigure,
                    (value, token) => service.ConfigureAsync(value.AppId, value.ToolId, value.ProxyName, value.Settings,
                        token), "The settings are invalid."),
                SteamUiModuleBuilder.Command<(uint AppId, string ToolId)>(PatchId, "remove", TryTool,
                    (value, token) => service.RemoveAsync(value.AppId, value.ToolId, token),
                    "The remove request is invalid.")
            ]);
    }

    private static bool AppId(JsonElement payload, out uint appId)
    {
        appId = 0;
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("appId", out var id)
                                                         && id.ValueKind == JsonValueKind.Number
                                                         && id.TryGetUInt32(out appId) && appId > 0;
    }

    // Every property must be one of the command's names; required ones are checked by the readers.
    private static bool Only(JsonElement payload, params string[] names)
    {
        return payload.ValueKind == JsonValueKind.Object
               && payload.EnumerateObject().All(property => names.Contains(property.Name, StringComparer.Ordinal));
    }

    private static bool TryAppId(JsonElement payload, out uint appId)
    {
        return AppId(payload, out appId) && SteamUiPayload.HasExactly(payload, 1);
    }

    private static bool TryRuntime(JsonElement payload, out (uint AppId, string Path) value)
    {
        value = default;
        if (!AppId(payload, out var appId) || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path)
                                           || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        value = (appId, path);
        return true;
    }

    private static bool TryTool(JsonElement payload, out (uint AppId, string ToolId) value)
    {
        value = default;
        if (!AppId(payload, out var appId) || !SteamUiPayload.TryReadNonBlankString(payload, "toolId", out var toolId)
                                           || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        value = (appId, toolId);
        return true;
    }

    private static bool TryUpdate(JsonElement payload, out (uint AppId, string ToolId, bool ReplaceConfig) value)
    {
        value = default;
        if (!Only(payload, "appId", "toolId", "replaceConfig") || !AppId(payload, out var appId)
                                                              || !SteamUiPayload.TryReadNonBlankString(payload, "toolId",
                                                                  out var toolId)
                                                              || !OptionalBoolean(payload, "replaceConfig",
                                                                  out var replaceConfig))
        {
            return false;
        }

        value = (appId, toolId, replaceConfig);
        return true;
    }

    private static bool TryInstall(JsonElement payload, out GameToolsInstallRequest value)
    {
        value = null!;
        if (!Only(payload, "appId", "toolId", "proxyName", "settings", "acknowledgeRisk", "replaceExisting")
            || !AppId(payload, out var appId)
            || !SteamUiPayload.TryReadNonBlankString(payload, "toolId", out var toolId)
            || !OptionalName(payload, "proxyName", out var proxyName)
            || !Settings(payload, out var settings)
            || !OptionalBoolean(payload, "acknowledgeRisk", out var acknowledge)
            || !OptionalBoolean(payload, "replaceExisting", out var replace))
        {
            return false;
        }

        value = new GameToolsInstallRequest(appId, toolId, proxyName, settings, acknowledge, replace);
        return true;
    }

    private static bool TryConfigure(JsonElement payload,
        out (uint AppId, string ToolId, string? ProxyName, IReadOnlyDictionary<string, string> Settings) value)
    {
        value = default;
        if (!Only(payload, "appId", "toolId", "proxyName", "settings") || !AppId(payload, out var appId)
                                                                       || !SteamUiPayload.TryReadNonBlankString(payload,
                                                                           "toolId", out var toolId)
                                                                       || !OptionalName(payload, "proxyName",
                                                                           out var proxyName)
                                                                       || !Settings(payload, out var settings))
        {
            return false;
        }

        value = (appId, toolId, proxyName, settings);
        return true;
    }

    // Absent or null means false; the toolkit reader checks a present value.
    private static bool OptionalBoolean(JsonElement payload, string name, out bool value)
    {
        value = false;
        return !payload.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
                                                               || SteamUiPayload.TryReadBoolean(payload, name, out value);
    }

    // Absent means null; the toolkit reader accepts null or a non-blank string.
    private static bool OptionalName(JsonElement payload, string name, out string? value)
    {
        value = null;
        return !payload.TryGetProperty(name, out _) || SteamUiPayload.TryReadNullableString(payload, name, out value);
    }

    private static bool Settings(JsonElement payload, out IReadOnlyDictionary<string, string> settings)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        settings = values;
        if (!payload.TryGetProperty("settings", out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var entry in property.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String || values.Count >= 32)
            {
                return false;
            }

            values[entry.Name] = entry.Value.GetString()!;
        }

        return true;
    }
}
