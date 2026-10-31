using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>Notebook identities.</summary>
/// <remarks>
///     <list type="bullet">
///         <item><c>global</c>: the notebook that belongs to no game.</item>
///         <item><c>steam:&lt;appid&gt;</c>: a Steam title, keyed by AppID.</item>
///         <item>
///             <c>library:&lt;source&gt;:&lt;key&gt;</c>: a title WSGM's Game Library imported, keyed by its
///             durable source identity, so recreating its Steam shortcut or moving it to another drive
///             keeps its notes.
///         </item>
///         <item><c>steam:&lt;shortcut appid&gt;</c>: a non-Steam shortcut WSGM did not create.</item>
///         <item><c>process:&lt;executable&gt;</c>: an application Steam does not know about.</item>
///     </list>
///     A title is never an identity.
/// </remarks>
public static partial class NoteScope
{
    /// <summary>The Global notebook.</summary>
    public const string Global = "global";

    /// <summary>The notebook of a Steam AppID, including a shortcut's generated id.</summary>
    /// <param name="appId">The app id.</param>
    /// <returns>The scope.</returns>
    public static string ForSteamApp(uint appId)
    {
        return "steam:" + appId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The notebook of a Game Library title.</summary>
    /// <param name="source">The library source id.</param>
    /// <param name="key">The title's stable key in that source.</param>
    /// <returns>The scope.</returns>
    public static string ForLibraryTitle(string source, string key)
    {
        return $"library:{source}:{key}";
    }

    /// <summary>Whether a value is a well-formed scope.</summary>
    /// <param name="scope">The candidate.</param>
    /// <returns>True for Global or a recognized, nonempty identity.</returns>
    public static bool IsValid(string? scope)
    {
        if (scope is null || scope.Length == 0 || scope.Any(char.IsControl))
        {
            return false;
        }

        return scope == Global
               || SteamPattern().IsMatch(scope)
               || (scope.StartsWith("library:", StringComparison.Ordinal) && scope.IndexOf(':', 8) > 8
                                                                         && !scope.EndsWith(':'))
               || (scope.StartsWith("process:", StringComparison.Ordinal) && scope.Length > 8);
    }

    [GeneratedRegex("^steam:[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamPattern();
}
