using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>Structural safety check for the user-authored patterns of regex library filters.</summary>
internal static partial class LibraryFilterPattern
{
    [GeneratedRegex(@"\\[1-9]")]
    private static partial Regex BackreferenceRegex();

    /// <summary>
    ///     Whether a pattern is one V8 can run over every title without exponential backtracking.
    ///     The rules are structural, so the answer never depends on machine load: no lookarounds or
    ///     inline options, no backreferences, and no quantified group whose body can match the same
    ///     text more than one way.
    /// </summary>
    /// <param name="pattern">The user-authored pattern.</param>
    /// <returns>Whether the pattern parses and cannot backtrack exponentially.</returns>
    internal static bool IsSafeRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)
            || pattern.Contains("(?", StringComparison.Ordinal)
            || BackreferenceRegex().IsMatch(pattern)
            || HasNestedQuantifier(pattern))
        {
            return false;
        }

        try
        {
            // Syntax only: a pattern .NET cannot parse is one the filter cannot describe either.
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    ///     True when a quantifier is applied to a group whose body is itself quantified, or a
    ///     repeated group holds an alternation: the (a+)+ and (a|ab)+ shapes whose backtracking is
    ///     exponential.
    /// </summary>
    /// <param name="pattern">The user-authored pattern.</param>
    private static bool HasNestedQuantifier(string pattern)
    {
        var starts = new Stack<int>();
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '[':
                    i = SkipCharacterClass(pattern, i);
                    break;
                case '(':
                    starts.Push(i);
                    break;
                case ')' when starts.Count > 0:
                {
                    var start = starts.Pop();
                    var next = i + 1 < pattern.Length ? pattern[i + 1] : '\0';
                    if (next is '*' or '+' or '{' or '?'
                        && IsAmbiguousBody(pattern.AsSpan(start + 1, i - start - 1), next is not '?'))
                    {
                        return true;
                    }

                    break;
                }
            }
        }

        return false;
    }

    private static int SkipCharacterClass(string pattern, int open)
    {
        for (var i = open + 1; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case ']':
                    return i;
            }
        }

        return pattern.Length - 1;
    }

    private static bool IsAmbiguousBody(ReadOnlySpan<char> body, bool repeated)
    {
        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '\\':
                    i++;
                    break;
                case '[':
                    while (i < body.Length && body[i] != ']')
                    {
                        if (body[i] == '\\')
                        {
                            i++;
                        }

                        i++;
                    }

                    break;
                case '*' or '+' or '{' or '?':
                case '|' when repeated:
                    return true;
            }
        }

        return false;
    }
}
