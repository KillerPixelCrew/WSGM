using System;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>Reading values out of a third party's JSON: a theme manifest, DeckThemes, SteamDeckRepo.</summary>
internal static class JsonRead
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

    /// <summary>A non-negative count, or zero when absent or not an integer.</summary>
    /// <param name="json">The object.</param>
    /// <param name="property">The property's name.</param>
    /// <returns>The count.</returns>
    internal static int Count(JsonElement json, string property)
    {
        return json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                                                            && value.TryGetInt32(out var count)
            ? Math.Max(0, count)
            : 0;
    }

    /// <summary>An https address of reasonable length, or empty for anything else.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns>The address, or empty.</returns>
    internal static string HttpsUrl(string? value)
    {
        return value is not null && value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                 && value.Length <= 2048
            ? value
            : string.Empty;
    }
}
