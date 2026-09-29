using System;
using System.Globalization;
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
    /// <remarks>
    ///     A count written as a string of digits is read too: SteamDeckRepo sends its likes and
    ///     downloads that way, and reading them as zero left its popularity sorts meaningless.
    /// </remarks>
    internal static int Count(JsonElement json, string property)
    {
        if (!json.TryGetProperty(property, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var count) => Math.Max(0, count),
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var count) => count,
            _ => 0
        };
    }

    /// <summary>An integer property, or the fallback when absent, not a number or out of range.</summary>
    /// <param name="json">The object.</param>
    /// <param name="property">The property's name.</param>
    /// <param name="fallback">What an unusable value reads as.</param>
    /// <returns>The integer.</returns>
    internal static int Int(JsonElement json, string property, int fallback)
    {
        return json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                                                            && value.TryGetInt32(out var number)
            ? number
            : fallback;
    }

    /// <summary>An https address, or empty for anything else.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns>The address, or empty.</returns>
    internal static string HttpsUrl(string? value)
    {
        return value is not null && value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? value
            : string.Empty;
    }
}
