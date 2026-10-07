using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

internal static partial class SteamLibraryImportSurface
{
    private static readonly GameLibraryJsonContext SourceJson = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    private static IReadOnlyList<SteamUiCommandHandler> ManagedCommands(IGameLibraryBackend backend)
    {
        return
        [
            SteamUiModuleBuilder.Command<RomSourceConfig>(PatchId, "addRomSource", TryReadSource,
                backend.AddRomSourceAsync, "The ROM source is invalid."),
            SteamUiModuleBuilder.Command<string>(PatchId, "removeRomSource", TryReadId,
                backend.RemoveRomSourceAsync, "The ROM source id is invalid."),
            SteamUiModuleBuilder.Command<ManualShortcutConfig>(PatchId, "addManualSource", TryReadSource,
                backend.AddManualSourceAsync, "The manual source is invalid."),
            SteamUiModuleBuilder.Command<string>(PatchId, "removeManualSource", TryReadId,
                backend.RemoveManualSourceAsync, "The manual source id is invalid."),
            SteamUiModuleBuilder.Command<EmulatorChoice>(PatchId, "setRomEmulator", TryReadEmulatorChoice,
                (value, token) => backend.SetRomEmulatorAsync(value.Id, value.InstallationId, value.CoreId, token),
                "The emulator choice is invalid."),
            SteamUiModuleBuilder.Command<TitleRequest>(PatchId, "setRomTitle", TryReadTitle,
                (value, token) => backend.SetRomTitleAsync(value.Id, value.Name, token),
                "The title is invalid."),
            SteamUiModuleBuilder.Command<ArgumentRequest>(PatchId, "setRomArguments", TryReadArguments,
                (value, token) => backend.SetRomArgumentsAsync(value.Id, value.Arguments, token),
                "The launch arguments are invalid."),
            SteamUiModuleBuilder.Command<SourceRequest>(PatchId, "setCleanup", TryReadSource,
                (value, token) => backend.SetCleanupAsync(value.Id, value.Enabled, token),
                "The removal choice is invalid."),
            SteamUiModuleBuilder.Command<string>(PatchId, "recheckAvailability", TryReadRecheck,
                backend.RecheckAvailabilityAsync, "The content id is invalid.")
        ];
    }

    private static bool TryReadSource<T>(JsonElement payload, out T value) where T : class, new()
    {
        value = new T();
        if (!SteamUiPayload.HasExactly(payload, 1)
            || !payload.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in source.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }
                }
            }
        }

        try
        {
            var read = source.Deserialize((JsonTypeInfo<T>)SourceJson.GetTypeInfo(typeof(T))!);
            if (read is null)
            {
                return false;
            }

            value = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadRecheck(JsonElement payload, out string id)
    {
        id = "";
        return SteamUiPayload.HasExactly(payload, 1) && SteamUiPayload.TryReadString(payload, "id", out id);
    }

    private static bool TryReadTitle(JsonElement payload, out TitleRequest value)
    {
        value = default;
        if (!TryReadIdAnd(payload, "name", 2, out var id, out var name))
        {
            return false;
        }

        value = new TitleRequest(id, name);
        return true;
    }

    private static bool TryReadArguments(JsonElement payload, out ArgumentRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "id", out var id)
            || !payload.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<string> values = [];
        foreach (var argument in arguments.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var text = argument.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        value = new ArgumentRequest(id, values);
        return true;
    }

    private static bool TryReadEmulatorChoice(JsonElement payload, out EmulatorChoice value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadNonBlankString(payload, "id", out var id)
            || !SteamUiPayload.TryReadString(payload, "installationId", out var installation)
            || !SteamUiPayload.TryReadString(payload, "coreId", out var core))
        {
            return false;
        }

        value = new EmulatorChoice(id, installation, core);
        return true;
    }

    private readonly record struct TitleRequest(string Id, string Name);

    private readonly record struct ArgumentRequest(string Id, IReadOnlyList<string> Arguments);

    private readonly record struct EmulatorChoice(string Id, string InstallationId, string CoreId);
}
