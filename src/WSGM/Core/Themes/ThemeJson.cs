using System.Text.Json;

namespace WSGM.Core;

/// <summary>Reading strings out of a third party's JSON: a manifest's, the store's.</summary>
internal static class ThemeJson
{
    /// <summary>A string property, or null when absent or not a string.</summary>
    /// <param name="json">The object.</param>
    /// <param name="property">The property's name.</param>
    /// <returns>The string, or null.</returns>
    internal static string? OptionalString(JsonElement json, string property)
    {
        return json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>A string property a manifest must carry.</summary>
    /// <param name="json">The object.</param>
    /// <param name="property">The property's name.</param>
    /// <returns>The string.</returns>
    /// <exception cref="ThemeManifestException">The property is absent or not a string.</exception>
    internal static string RequiredString(JsonElement json, string property)
    {
        return OptionalString(json, property) ?? throw new ThemeManifestException($"'{property}'");
    }
}
