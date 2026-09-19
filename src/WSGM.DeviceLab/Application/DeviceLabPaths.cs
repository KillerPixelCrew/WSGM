using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WSGM.DeviceLab.Application;

/// <summary>Builds collision-resistant filesystem names for Device Lab artifacts.</summary>
internal static class DeviceLabPaths
{
    /// <summary>Turns an identifier into a filesystem-safe name with an identifier-specific suffix.</summary>
    /// <param name="value">Identifier to represent.</param>
    /// <param name="allowDot">Whether dots may be retained in the readable portion.</param>
    /// <returns>A readable sanitized name with a stable hash suffix.</returns>
    /// <remarks>
    ///     The suffix keeps identifiers that sanitize to the same readable text from replacing one
    ///     another when they are used as artifact keys.
    /// </remarks>
    internal static string SafeName(string value, bool allowDot)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sanitized = string.Concat(value.Select(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_'
            || allowDot && character == '.'
                ? character
                : '-'));
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "unknown";
        }

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        return $"{sanitized}-{digest}";
    }
}
