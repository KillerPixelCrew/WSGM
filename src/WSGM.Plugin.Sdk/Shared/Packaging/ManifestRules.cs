using System;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Sdk.Packaging;

/// <summary>Pure identity, version and entry-point validation for common plugin manifests.</summary>
public static class ManifestRules
{
    /// <summary>
    ///     Whether a value is a package identifier: lowercase ASCII letters, digits, <c>.</c>, <c>-</c> and
    ///     <c>_</c>, starting with a letter or digit.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <returns><see langword="true" /> for a valid identifier.</returns>
    /// <remarks>Identifiers name state folders on a case-insensitive file system, so uppercase is refused.</remarks>
    public static bool IsPackageIdentifier(string? value)
    {
        return !string.IsNullOrEmpty(value)
               && char.IsAsciiLetterOrDigit(value[0])
               && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
    }

    /// <summary>Whether a value is a canonical dotted numeric version such as <c>1.2.0</c>.</summary>
    /// <param name="value">The candidate version.</param>
    /// <returns><see langword="true" /> when the value parses and prints back unchanged.</returns>
    public static bool IsCanonicalVersion(string? value)
    {
        return Version.TryParse(value, out var parsed)
               && parsed.ToString(parsed.Revision >= 0 ? 4 : parsed.Build >= 0 ? 3 : 2) == value;
    }

    /// <summary>
    ///     Whether a value is a <c>.dll</c> file name at the package root: ASCII letters, digits, <c>.</c>,
    ///     <c>_</c> and <c>-</c>, not starting with <c>.</c>.
    /// </summary>
    /// <param name="value">The candidate entry assembly.</param>
    /// <returns><see langword="true" /> for a root assembly file name.</returns>
    public static bool IsRootAssemblyFileName(string? value)
    {
        return !string.IsNullOrEmpty(value)
               && value[0] != '.'
               && value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    /// <summary>
    ///     Whether a value is a namespace-qualified type name of ASCII letters, digits, <c>.</c>, <c>_</c> and
    ///     <c>+</c>. A generic type name is refused, because an open generic type cannot be activated.
    /// </summary>
    /// <param name="value">The candidate entry type.</param>
    /// <returns><see langword="true" /> for a valid entry type name.</returns>
    public static bool IsEntryTypeName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+');
    }

    /// <summary>Whether a package name is plain text that is safe to display and log.</summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="error">Why it is not, when the result is <see langword="false" />.</param>
    /// <returns><see langword="true" /> for a valid name.</returns>
    public static bool TryValidateName(string? value, out string? error)
    {
        return PlainText.TryValidate(value, "name", out error);
    }
}
