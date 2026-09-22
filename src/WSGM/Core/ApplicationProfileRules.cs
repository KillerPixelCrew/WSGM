using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Shared activation rules for performance and device profile consumers.</summary>
internal static class ApplicationProfileRules
{
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

    internal static string[] ValidateProcesses(IEnumerable<string> names)
    {
        var result = names.Select(name => name.Trim()).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length > 32 || result.Any(name => name.Length > 128
                                                     || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                     || name.IndexOfAny([
                                                         '/', '\\', ':', '*', '?', '"', '<', '>', '|'
                                                     ]) >= 0
                                                     || name.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "Enter up to 32 executable names such as game.exe, without paths or wildcards.");
        }

        return result;
    }
}
