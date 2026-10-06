using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Win32;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Core;

/// <summary>
///     Repairs enum values from generated metadata and the field's own default template, and moves an older
///     stored document to the current schema.
/// </summary>
internal static class ConfigRepair
{
    /// <summary>
    ///     Parses a stored document. Its <see cref="AppConfig.SchemaVersion" /> is the file's own, 0 when the
    ///     file has none.
    /// </summary>
    /// <param name="json">The stored text.</param>
    /// <returns>The repaired document, not yet normalized or migrated.</returns>
    internal static AppConfig Deserialize(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException("Configuration root was not an object.");

        // 2.0 wrote no version; a hand-edited value that is not a whole number reads as 2.0 too.
        if (root[nameof(AppConfig.SchemaVersion)] is not JsonValue version || !version.TryGetValue<int>(out _))
        {
            root[nameof(AppConfig.SchemaVersion)] = 0;
        }

        RepairJson(root, typeof(AppConfig), AppConfigDefaults.Defaults);
        return root.Deserialize(ConfigJsonContext.Tolerant.AppConfig)
               ?? throw new JsonException("Configuration contained null instead of an object.");
    }

    /// <summary>
    ///     Moves a normalized document to <see cref="AppConfig.CurrentSchemaVersion" /> in memory. Only stored
    ///     values whose meaning changed are rewritten; the next strict write persists them. A document from a
    ///     newer WSGM is loaded best effort as it is.
    /// </summary>
    /// <param name="config">The normalized document, changed in place.</param>
    /// <returns>The schema version the file was written with.</returns>
    internal static int Migrate(AppConfig config)
    {
        var stored = config.SchemaVersion;
        if (stored > AppConfig.CurrentSchemaVersion)
        {
            Log.Warn($"config.json uses schema {stored} from a newer WSGM; settings this build does not know are "
                     + "dropped at the next save.");
        }

        if (stored < 1)
        {
            // 2.0 saved every editor-made layout with rotation 1 because the editor has no rotation control.
            // Rotation 0 keeps each display's current rotation, which is what those layouts meant. The pending
            // return layout is a capture of the real desktop and keeps its exact rotation.
            config.GameModeLaunch.GameLayout = KeepCurrentRotation(config.GameModeLaunch.GameLayout);
            config.GameModeLaunch.DesktopLayout = KeepCurrentRotation(config.GameModeLaunch.DesktopLayout);
        }

        config.SchemaVersion = AppConfig.CurrentSchemaVersion;
        return stored;
    }

    private static DisplayLayout? KeepCurrentRotation(DisplayLayout? layout)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (layout?.Outputs is null || !layout.Outputs.Any(static output => output is { Rotation: 1 }))
        {
            return layout;
        }

        return layout with
        {
            Outputs =
            [
                .. layout.Outputs.Select(static output =>
                    output is { Rotation: 1 } ? output with { Rotation = 0 } : output)
            ]
        };
    }

    internal static void NormalizeEnums(object value)
    {
        NormalizeObject(value, value.GetType(), null, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static JsonNode? RepairJson(JsonNode? node, Type declaredType, object? template)
    {
        var nullable = Nullable.GetUnderlyingType(declaredType);
        var type = nullable ?? declaredType;
        if (type.IsEnum)
        {
            if (node is null)
            {
                return nullable is not null ? null : EnumNode(type, template);
            }

            if (node is not JsonValue value ||
                value.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
            {
                return node;
            }

            var text = value.TryGetValue<string>(out var name) ? name : value.ToJsonString();
            if (Enum.TryParse(type, text, true, out var parsed) && IsValid(type, parsed!))
            {
                return node;
            }

            // Oversized numeric representations remain parse errors, not invented preferences.
            if (value.GetValueKind() is JsonValueKind.Number && parsed is null)
            {
                return node;
            }

            return nullable is not null ? null : EnumNode(type, template);
        }

        if (node is null || ConfigJsonContext.Tolerant.GetTypeInfo(type) is not { } metadata)
        {
            return node;
        }

        template = Template(type, template, metadata);
        if (metadata.Kind is JsonTypeInfoKind.Object && node is JsonObject obj)
        {
            foreach (var property in metadata.Properties)
            {
                if (!obj.TryGetPropertyValue(property.Name, out var child))
                {
                    continue;
                }

                var fallback = template is null ? null : property.Get?.Invoke(template);
                var repaired = RepairJson(child, property.PropertyType, fallback);
                if (!ReferenceEquals(child, repaired))
                {
                    obj[property.Name] = repaired;
                }
            }
        }
        else if (metadata.Kind is JsonTypeInfoKind.Enumerable && node is JsonArray array
                                                              && ElementType(type) is { } element)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var child = array[index];
                var repaired = RepairJson(child, element, null);
                if (!ReferenceEquals(child, repaired))
                {
                    array[index] = repaired;
                }
            }
        }
        else if (metadata.Kind is JsonTypeInfoKind.Dictionary && node is JsonObject dictionary
                                                              && DictionaryValueType(type) is { } valueType)
        {
            var keyType = (Generic(type, typeof(IDictionary<,>)) ?? Generic(type, typeof(IReadOnlyDictionary<,>)))
                ?.GetGenericArguments()[0];
            foreach (var key in dictionary.Select(pair => pair.Key).ToArray())
            {
                if (keyType?.IsEnum is true
                    && (!Enum.TryParse(keyType, key, true, out var parsed) || !IsValid(keyType, parsed!)))
                {
                    dictionary.Remove(key);
                    continue;
                }

                var child = dictionary[key];
                var repaired = RepairJson(child, valueType, null);
                if (!ReferenceEquals(child, repaired))
                {
                    dictionary[key] = repaired;
                }
            }
        }

        return node;
    }

    private static bool NormalizeObject(object value, Type type, object? template, HashSet<object> visited)
    {
        // Cached SDK declarations are immutable snapshots. DeviceConfigurationRules validates
        // the whole declaration and drops malformed ones without discarding saved settings.
        if (value is PluginSettingsManifest)
        {
            return false;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!visited.Add(value) || ConfigJsonContext.Tolerant.GetTypeInfo(type) is not { } metadata)
        {
            return false;
        }

        var changed = false;
        template = Template(type, template, metadata);
        if (metadata.Kind is JsonTypeInfoKind.Object)
        {
            foreach (var property in metadata.Properties)
            {
                var current = property.Get?.Invoke(value);
                if (current is null)
                {
                    continue;
                }

                var underlying = Nullable.GetUnderlyingType(property.PropertyType);
                var memberType = underlying ?? property.PropertyType;
                var fallback = template is null ? null : property.Get?.Invoke(template);
                if (memberType.IsEnum)
                {
                    if (!IsValid(memberType, current) && property.Set is { } set)
                    {
                        set(value, underlying is not null
                            ? null
                            : fallback ?? Activator.CreateInstance(memberType));
                        changed = true;
                    }
                }
                else
                {
                    var memberChanged = NormalizeObject(current, memberType, fallback, visited);
                    // Generated setters for init-only record members throw, even for unchanged
                    // scalars. Only a repaired boxed struct needs to be written back.
                    if (memberChanged && memberType.IsValueType)
                    {
                        property.Set?.Invoke(value, current);
                    }

                    changed |= memberChanged;
                }
            }
        }
        else if (metadata.Kind is JsonTypeInfoKind.Enumerable && value is IEnumerable sequence
                                                              && ElementType(type) is { } element)
        {
            foreach (var child in sequence)
            {
                if (child is not null)
                {
                    changed |= NormalizeObject(child, element, null, visited);
                }
            }
        }
        else if (metadata.Kind is JsonTypeInfoKind.Dictionary && value is IDictionary dictionary
                                                              && DictionaryValueType(type) is { } valueType)
        {
            foreach (var child in dictionary.Values)
            {
                if (child is not null)
                {
                    changed |= NormalizeObject(child, valueType, null, visited);
                }
            }
        }

        return changed;
    }

    private static object? Template(Type type, object? supplied, JsonTypeInfo metadata)
    {
        if (type == typeof(AppConfig))
        {
            return AppConfigDefaults.Defaults;
        }

        if (type == typeof(SplashConfig))
        {
            return AppConfigDefaults.SplashFieldDefaults;
        }

        if (type == typeof(SplashElementPlacement))
        {
            return AppConfigDefaults.PlacementDefaults;
        }

        if (type == typeof(FilterNode))
        {
            return AppConfigDefaults.FilterDefaults;
        }

        return supplied ?? metadata.CreateObject?.Invoke();
    }

    private static JsonNode? EnumNode(Type type, object? fallback)
    {
        var value = IsRecovery(type) ? Enum.ToObject(type, int.MaxValue) : fallback ?? Activator.CreateInstance(type);
        return JsonSerializer.SerializeToNode(value, type,
            ConfigJsonContext.Tolerant.Options);
    }

    private static bool IsValid(Type type, object value)
    {
        // Recovery numbers are deliberately bounded when used, rather than corrupting the file.
        if (IsRecovery(type))
        {
            return true;
        }

        if (!type.IsDefined(typeof(FlagsAttribute), false))
        {
            return Enum.IsDefined(type, value);
        }

        ulong allowed = 0;
        foreach (var defined in Enum.GetValues(type))
        {
            allowed |= Bits(type, defined);
        }

        return (Bits(type, value) & ~allowed) == 0;
    }

    internal static bool IsRecovery(Type type)
    {
        return type == typeof(SteamAutostartKind) || type == typeof(SteamAutostartScope)
                                                  || type == typeof(RegistryValueKind);
    }

    private static ulong Bits(Type type, object value)
    {
        var underlying = Enum.GetUnderlyingType(type);
        return underlying == typeof(ulong) || underlying == typeof(uint) || underlying == typeof(ushort)
               || underlying == typeof(byte)
            ? Convert.ToUInt64(value)
            : unchecked((ulong)Convert.ToInt64(value));
    }

    private static Type? ElementType(Type type)
    {
        return type.IsArray ? type.GetElementType() : Generic(type, typeof(IEnumerable<>))?.GetGenericArguments()[0];
    }

    private static Type? DictionaryValueType(Type type)
    {
        return (Generic(type, typeof(IDictionary<,>)) ?? Generic(type, typeof(IReadOnlyDictionary<,>)))
            ?.GetGenericArguments()[1];
    }

    private static Type? Generic(Type type, Type definition)
    {
        return type.GetInterfaces().Prepend(type)
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == definition);
    }
}
