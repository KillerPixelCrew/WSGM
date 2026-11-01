using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>Replaces the Windows user folder and user name in a log before it leaves the machine.</summary>
/// <remarks>
///     <para>
///         The user folder becomes <c>%USERPROFILE%</c> wherever it appears, in any case and with either slash, a
///         doubled backslash included, as long as the folder name is not the start of a longer name. The user
///         name, and the folder's own name when it differs (a Microsoft account's folder is often not the account
///         name), then become <c>&lt;user&gt;</c> where they stand as a whole word, which covers every path
///         segment. A name shorter than three characters is replaced only as a path segment, so a user called
///         "a" does not turn every article into <c>&lt;user&gt;</c>.
///     </para>
///     <para>
///         Everything is replaced in one pass, so a replacement is never matched again: a user called "user"
///         still reads <c>&lt;user&gt;</c>. Nothing else is touched. The log is the maintainer's diagnosis
///         source, so game names, the device and hardware details stay exactly as written.
///     </para>
/// </remarks>
internal sealed class LogMask
{
    /// <summary>What the user folder becomes.</summary>
    internal const string ProfileToken = "%USERPROFILE%";

    /// <summary>What the user name becomes.</summary>
    internal const string UserToken = "<user>";

    // A letter, digit or underscore beside a name means it is part of a longer word; a hyphen after the folder
    // means it was only the start of a longer folder name.
    private const string Word = @"[\p{L}\p{N}_]";

    private readonly Regex? _pattern;

    /// <summary>Creates a mask for one account.</summary>
    /// <param name="profile">The user folder, such as <c>C:\Users\chris</c>; empty masks no folder.</param>
    /// <param name="userName">The account name; empty masks no name.</param>
    internal LogMask(string profile, string userName)
    {
        List<string> alternatives = [];
        var segments = profile.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0)
        {
            var folder = string.Join(@"[\\/]+", segments.Select(Regex.Escape));
            alternatives.Add($@"(?<profile>{folder}(?!{Word}|-))");
        }

        List<string> names = [userName.Trim()];
        if (segments.Length > 1)
        {
            names.Add(segments[^1]);
        }

        alternatives.AddRange(names.Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => name.Length < 3
                ? $@"(?<=[\\/]){Regex.Escape(name)}(?!{Word}|[\-.])"
                : $"(?<!{Word}){Regex.Escape(name)}(?!{Word})"));
        if (alternatives.Count > 0)
        {
            _pattern = new Regex(string.Join('|', alternatives),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
        }
    }

    /// <summary>A mask for the account this process runs as.</summary>
    /// <returns>The mask.</returns>
    internal static LogMask CurrentUser()
    {
        return new LogMask(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar),
            Environment.UserName);
    }

    /// <summary>Masks one text.</summary>
    /// <param name="text">The log text.</param>
    /// <returns>The text with the user folder and name replaced.</returns>
    internal string Apply(string text)
    {
        return _pattern?.Replace(text, match => match.Groups["profile"].Success ? ProfileToken : UserToken) ?? text;
    }
}
