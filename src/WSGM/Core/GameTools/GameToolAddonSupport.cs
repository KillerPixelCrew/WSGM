using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>The games an add-on supports, read from its publisher's source.</summary>
/// <param name="Executables">Supported executable names in lowercase, or null when the list couldn't be read.</param>
/// <param name="Error">Why the list couldn't be read, or null.</param>
/// <param name="CheckedUtc">When it was read.</param>
internal sealed record GameToolAddonSupport(IReadOnlySet<string>? Executables, string? Error, DateTime CheckedUtc)
{
    /// <summary>Reads every supported executable name from a source file, as OptiScaler's setup script does.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="rules">The add-on's rules.</param>
    /// <returns>The names in lowercase.</returns>
    internal static HashSet<string> Parse(string source, IEnumerable<GameToolAddonSupportRule> rules)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            foreach (Match match in Regex.Matches(source, rule.Pattern, RegexOptions.CultureInvariant,
                         TimeSpan.FromSeconds(1)))
            {
                var captured = match.Groups[1].Value;
                if (captured.Length == 0)
                {
                    continue;
                }

                foreach (var name in rule.Names)
                {
                    names.Add(name.Replace("{0}", captured, StringComparison.Ordinal).ToLowerInvariant());
                }
            }
        }

        return names;
    }

    /// <summary>Whether a program is on the list; the add-on checks the running program's own file name.</summary>
    /// <param name="executable">The chosen program's file name.</param>
    /// <returns>True when listed; false when not listed or the list is unknown.</returns>
    internal bool Supports(string executable)
    {
        return Executables?.Contains(executable.ToLowerInvariant()) == true;
    }
}
