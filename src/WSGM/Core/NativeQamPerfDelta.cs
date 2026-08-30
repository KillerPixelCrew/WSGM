using System.Collections.Generic;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>One setting change Steam's performance panel asked WSGM to make.</summary>
/// <param name="Kind">Which setting changed.</param>
/// <param name="Value">The requested value; meaning depends on <paramref name="Kind"/>.</param>
internal readonly record struct NativeQamPerfChange(NativeQamPerfSetting Kind, int Value)
{
    /// <summary>Reads the change as a flag.</summary>
    internal bool AsFlag => Value != 0;
}

/// <summary>The performance settings WSGM accepts writes for.</summary>
/// <remarks>
/// Only the settings behind a control WSGM actually mounts. A delta naming anything else is
/// reported as unsupported rather than silently dropped, because a control that appears to work and
/// does nothing is worse than one that is not there.
/// </remarks>
internal enum NativeQamPerfSetting
{
    /// <summary>The frame cap in FPS.</summary>
    FrameLimit,

    /// <summary>Whether the frame cap applies.</summary>
    FrameLimitEnabled,

    /// <summary>The performance overlay level.</summary>
    OverlayLevel,

    /// <summary>Whether variable refresh rate is on.</summary>
    VariableRefreshRate,

    /// <summary>The manually chosen refresh rate in Hz.</summary>
    RefreshRateHz,

    /// <summary>Whether the running application keeps its own profile.</summary>
    PerApplicationProfileEnabled,

    /// <summary>Whether the advanced rows are shown.</summary>
    AdvancedSettingsEnabled,
}

/// <summary>What one <c>UpdateSettings</c> call asked for.</summary>
/// <param name="Recognized">Changes WSGM can apply, in the order they appeared.</param>
/// <param name="ResetToDefault">Whether the panel asked to reset the current profile.</param>
/// <param name="SteamAppId">The AppID the delta targets, or null for the global profile.</param>
/// <param name="Unsupported">
/// Field names that were present and are not backed, for the log. Never empty silently.
/// </param>
internal sealed record NativeQamPerfDelta(
    IReadOnlyList<NativeQamPerfChange> Recognized,
    bool ResetToDefault,
    uint? SteamAppId,
    IReadOnlyList<string> Unsupported);

/// <summary>
/// Decodes a <c>CMsgSystemPerfUpdateSettings</c> that the injected shim forwarded as an object.
/// </summary>
/// <remarks>
/// Every setter in Valve's store builds a delta and hands it to the one <c>UpdateSettings</c>
/// method, so this is where all of them arrive. The message shapes belong to the client, so the
/// injected half forwards <c>toObject()</c> verbatim and this half does the interpreting; nothing
/// about the wire format is reimplemented on either side.
/// <para>
/// A delta carries only what changed, and a settings message nests
/// <c>settings_delta.global</c>/<c>settings_delta.per_app</c>. Both are optional and either may be
/// absent on any given call.
/// </para>
/// </remarks>
internal static class NativeQamPerfDeltaReader
{
    /// <summary>Reads a forwarded update-settings payload.</summary>
    /// <param name="payload">The request payload, expected to carry a <c>delta</c> object.</param>
    /// <param name="delta">The decoded delta when this returns true.</param>
    /// <param name="error">Why the payload could not be read, when this returns false.</param>
    /// <returns>Whether the payload was a readable delta.</returns>
    internal static bool TryRead(
        JsonElement payload,
        out NativeQamPerfDelta delta,
        out string? error)
    {
        delta = new NativeQamPerfDelta([], false, null, []);
        error = null;

        if (payload.ValueKind is not JsonValueKind.Object
            || !payload.TryGetProperty("delta", out JsonElement message)
            || message.ValueKind is not JsonValueKind.Object)
        {
            error = "The performance delta payload carried no delta object.";
            return false;
        }

        List<NativeQamPerfChange> recognized = [];
        List<string> unsupported = [];

        bool resetToDefault = ReadFlag(message, "reset_to_default") ?? false;
        uint? steamAppId = ReadAppId(message);

        if (message.TryGetProperty("settings_delta", out JsonElement settings)
            && settings.ValueKind is JsonValueKind.Object)
        {
            if (settings.TryGetProperty("global", out JsonElement global)
                && global.ValueKind is JsonValueKind.Object)
            {
                ReadFields(global, recognized, unsupported);
            }

            if (settings.TryGetProperty("per_app", out JsonElement perApp)
                && perApp.ValueKind is JsonValueKind.Object)
            {
                ReadFields(perApp, recognized, unsupported);
            }
        }

        delta = new NativeQamPerfDelta(recognized, resetToDefault, steamAppId, unsupported);
        return true;
    }

    private static void ReadFields(
        JsonElement settings,
        List<NativeQamPerfChange> recognized,
        List<string> unsupported)
    {
        foreach (JsonProperty property in settings.EnumerateObject())
        {
            // toObject() emits every field of the message, not only the ones the setter touched, so
            // a null or absent value is "not part of this delta" and must not be applied. Treating
            // them as changes would make one slider write every other control's current value back
            // on every drag.
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            NativeQamPerfSetting? kind = property.Name switch
            {
                "fps_limit" => NativeQamPerfSetting.FrameLimit,
                "is_fps_limit_enabled" => NativeQamPerfSetting.FrameLimitEnabled,
                "perf_overlay_level" => NativeQamPerfSetting.OverlayLevel,
                "is_vrr_enabled" => NativeQamPerfSetting.VariableRefreshRate,
                "display_refresh_manual_hz" => NativeQamPerfSetting.RefreshRateHz,
                "is_game_perf_profile_enabled" => NativeQamPerfSetting.PerApplicationProfileEnabled,
                "is_advanced_settings_enabled" => NativeQamPerfSetting.AdvancedSettingsEnabled,
                _ => null,
            };

            if (kind is not { } setting)
            {
                unsupported.Add(property.Name);
                continue;
            }

            if (TryReadInteger(property.Value, out int value))
            {
                recognized.Add(new NativeQamPerfChange(setting, value));
            }
            else
            {
                unsupported.Add(property.Name);
            }
        }
    }

    private static bool TryReadInteger(JsonElement value, out int result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                result = 1;
                return true;
            case JsonValueKind.False:
                result = 0;
                return true;
            case JsonValueKind.Number when value.TryGetInt32(out result):
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool? ReadFlag(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <remarks>
    /// <c>gameid</c> is a 64-bit id, and the client emits it as either a number or a string
    /// depending on magnitude. Anything that is not a Steam AppID — zero, or a value beyond 32 bits
    /// such as a full game id — targets the global profile rather than being guessed at.
    /// </remarks>
    private static uint? ReadAppId(JsonElement message)
    {
        if (!message.TryGetProperty("gameid", out JsonElement value))
        {
            return null;
        }

        ulong raw = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt64(out ulong number) => number,
            JsonValueKind.String when ulong.TryParse(value.GetString(), out ulong parsed) => parsed,
            _ => 0,
        };

        return raw is > 0 and <= uint.MaxValue ? (uint)raw : null;
    }
}
