using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WSGM.Core;

/// <summary>Creates isolated configuration copies through the source-generated persistence contract.</summary>
internal static class ConfigJson
{
    /// <summary>
    ///     Deep-copies one configuration document through the production JSON
    ///     contract — the one clone mechanism for config shapes, so a copy can never
    ///     diverge from what a save/load round trip would produce.
    /// </summary>
    /// <typeparam name="T">A type registered on <see cref="ConfigJsonContext" />.</typeparam>
    /// <param name="value">The instance to copy.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T" />.</param>
    /// <returns>An isolated copy sharing no mutable state with <paramref name="value" />.</returns>
    internal static T Clone<T>(T value, JsonTypeInfo<T> typeInfo)
        where T : class, new()
    {
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(value, typeInfo), typeInfo) ?? new T();
    }
}
