using System;
using System.Collections.Generic;
using System.IO.Enumeration;
using System.Linq;

namespace WSGM.Core;

/// <summary>Recognises installed anti-cheat systems from their files.</summary>
/// <remarks>
///     Heuristic: these markers are the common install layouts of each system, not a guarantee. A game
///     may ship an anti-cheat it does not enforce offline, or enforce one whose files live elsewhere. The
///     result only decides whether WSGM asks for an explicit acknowledgement.
/// </remarks>
internal static class AntiCheatScan
{
    private static readonly (string Pattern, string Name)[] DirectoryMarkers =
    [
        ("EasyAntiCheat", "Easy Anti-Cheat"),
        ("EasyAntiCheat_EOS*", "Easy Anti-Cheat"),
        ("BattlEye", "BattlEye"),
        ("AntiCheatExpert", "Anti-Cheat Expert"),
        ("ACE-*", "Anti-Cheat Expert"),
        ("EAAntiCheat*", "EA Anti-Cheat")
    ];

    private static readonly (string Pattern, string Name)[] FileMarkers =
    [
        ("start_protected_game.exe", "Easy Anti-Cheat"),
        ("EasyAntiCheat*.exe", "Easy Anti-Cheat"),
        ("EasyAntiCheat*.sys", "Easy Anti-Cheat"),
        ("BEService*.exe", "BattlEye"),
        ("*_BE.exe", "BattlEye"),
        ("EAAntiCheat*", "EA Anti-Cheat"),
        ("ACE-*", "Anti-Cheat Expert")
    ];

    /// <summary>Names the detected systems.</summary>
    /// <param name="layout">The walked install folder.</param>
    /// <returns>Distinct system names, sorted.</returns>
    internal static IReadOnlyList<string> Scan(GameFolderLayout layout)
    {
        SortedSet<string> found = new(StringComparer.Ordinal);
        foreach (var directory in layout.Directories)
        {
            var name = directory.Contains('/') ? directory[(directory.LastIndexOf('/') + 1)..] : directory;
            foreach (var marker in DirectoryMarkers.Where(marker =>
                         FileSystemName.MatchesSimpleExpression(marker.Pattern, name)))
            {
                found.Add(marker.Name);
            }
        }

        foreach (var file in layout.Executables.Concat(layout.Drivers).Concat(layout.Libraries))
        {
            foreach (var marker in FileMarkers.Where(marker =>
                         FileSystemName.MatchesSimpleExpression(marker.Pattern, file.Name)))
            {
                found.Add(marker.Name);
            }
        }

        return [.. found];
    }
}
