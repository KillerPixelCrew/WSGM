using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Shared activation rules for performance and device profile consumers.</summary>
internal static class ApplicationProfileRules
{
    /// <summary>Selects an unambiguous executable-specific profile, then an application-level fallback.</summary>
    /// <typeparam name="T">Profile reference type.</typeparam>
    /// <param name="profiles">Re-enumerable profile collection in fallback preference order.</param>
    /// <param name="applicationId">Active application identity; null or empty disables selection.</param>
    /// <param name="executable">Executable basename to match case-insensitively; null cannot match a valid process list.</param>
    /// <param name="identity">Reads each profile's case-sensitive application identity.</param>
    /// <param name="processes">Reads each profile's executable names; an empty list marks an application fallback.</param>
    /// <returns>
    ///     The unique executable match or first application fallback; null for missing identity or ambiguous executable
    ///     matches.
    /// </returns>
    internal static T? Match<T>(IEnumerable<T> profiles, string? applicationId, string? executable,
        Func<T, string> identity, Func<T, IReadOnlyList<string>> processes) where T : class
    {
        if (string.IsNullOrEmpty(applicationId))
        {
            return null;
        }

        T? match = null;
        foreach (var profile in profiles)
        {
            if (!processes(profile).Contains(executable, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // An invalid hand-edited configuration must not select an arbitrary winner.
            if (match is not null)
            {
                return null;
            }

            match = profile;
        }

        return match ?? profiles.FirstOrDefault(profile => processes(profile).Count == 0
                                                           && identity(profile) == applicationId);
    }

    /// <summary>Normalizes and validates executable basenames used by profile matching.</summary>
    /// <param name="names">Non-null names to trim, remove blanks from, and deduplicate case-insensitively.</param>
    /// <returns>Executable names in first-occurrence order.</returns>
    /// <exception cref="ArgumentException">
    ///     A nonblank entry lacks .exe or contains path, wildcard, control, or invalid
    ///     filename characters.
    /// </exception>
    internal static string[] ValidateProcesses(IEnumerable<string> names)
    {
        var result = names.Select(name => name.Trim()).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Any(name => !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                               || name.IndexOfAny([
                                   '/', '\\', ':', '*', '?', '"', '<', '>', '|'
                               ]) >= 0
                               || name.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "Enter executable names such as game.exe, without paths or wildcards.");
        }

        return result;
    }
}
