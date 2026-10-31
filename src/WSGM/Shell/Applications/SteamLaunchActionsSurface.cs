using System;
using System.Text.Json;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The native game-menu submenu; every command carries its originating AppID.</summary>
internal static class SteamLaunchActionsSurface
{
    internal const string PatchId = "wsgm.launch-actions";
    internal const string Template = "launch-actions";

    internal static ISteamUiModule Module(Func<bool> enabled, LaunchActionService service)
    {
        return new SteamUiModule(Template,
            [SteamPagePatch.Create(PatchId, "launchActions", "wsgm-launch-actions-v2:native-submenu", "Launch Actions")],
            [SteamUiModuleBuilder.Publication(PatchId, enabled, service.ReadStateAsync,
                LaunchActionsJsonContext.Default.LaunchActionState)],
            [
                SteamUiModuleBuilder.Command<uint>(PatchId, "open", TryAppId, service.OpenAsync,
                    "A valid game AppID is required."),
                SteamUiModuleBuilder.Command<(uint AppId, LaunchWrapperMode Mode)>(PatchId, "apply", TryMode,
                    async (value, token) =>
                    {
                        var result = await service.ApplyAsync(value.AppId, value.Mode, token).ConfigureAwait(false);
                        return new SteamUiCommandResult(result.Ok, result.Detail);
                    }, "The launch action is invalid."),
                SteamUiModuleBuilder.Command<(uint AppId, string Path, string Arguments)>(PatchId, "custom", TryCustom,
                    async (value, token) =>
                    {
                        var result = await service.ApplyCustomAsync(value.AppId, value.Path, value.Arguments, token).ConfigureAwait(false);
                        return new SteamUiCommandResult(result.Ok, result.Detail);
                    }, "The custom launch action is invalid.")
            ]);
    }

    private static bool AppId(JsonElement payload, out uint appId)
    {
        appId = 0;
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("appId", out var id)
            && id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out appId) && appId > 0;
    }

    private static bool TryAppId(JsonElement payload, out uint appId)
    {
        return AppId(payload, out appId) && SteamUiPayload.HasExactly(payload, 1);
    }

    private static bool TryMode(JsonElement payload, out (uint AppId, LaunchWrapperMode Mode) value)
    {
        value = default;
        if (!AppId(payload, out var appId) || !SteamUiPayload.TryReadInt(payload, "mode", 0, 3, out var mode)
            || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        value = (appId, (LaunchWrapperMode)mode);
        return true;
    }

    private static bool TryCustom(JsonElement payload, out (uint AppId, string Path, string Arguments) value)
    {
        value = default;
        if (!AppId(payload, out var appId) || !SteamUiPayload.TryReadNonBlankString(payload, "path", out var path)
            || !SteamUiPayload.TryReadNullableString(payload, "arguments", out var arguments)
            || !SteamUiPayload.HasExactly(payload, 3))
        {
            return false;
        }

        value = (appId, path, arguments ?? "");
        return true;
    }
}
