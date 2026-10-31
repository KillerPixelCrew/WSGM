using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>
///     Finds the places Windows starts Steam from.
///     WSGM starts Steam itself so the client inherits WSGM's integrity, and a second Steam started by
///     Windows first would take that away silently. The matching is pure so it can be tested against
///     real command lines rather than this machine's own startup list.
/// </summary>
public static class SteamAutostartScanner
{
    /// <summary>The StartupApproved list names Windows keeps per surface.</summary>
    internal const string RunList = "Run";

    /// <summary>StartupApproved subkey for 32-bit Run entries.</summary>
    internal const string Run32List = "Run32";

    /// <summary>StartupApproved subkey for startup-folder shortcuts.</summary>
    internal const string StartupFolderList = "StartupFolder";

    /// <summary>Finds every startup source that launches Steam.</summary>
    /// <param name="system">The startup surfaces to read.</param>
    /// <param name="steamExePath">Steam's own executable path, or null when Steam is not installed.</param>
    /// <returns>The matching sources, ordered so the log reads the same way twice.</returns>
    public static IReadOnlyList<SteamAutostartSource> Scan(IAutostartSystem system, string? steamExePath)
    {
        ArgumentNullException.ThrowIfNull(system);
        List<SteamAutostartSource> found = [];
        foreach (var scope in new[] { SteamAutostartScope.User, SteamAutostartScope.Machine })
        {
            foreach (var wow64 in new[] { false, true })
            {
                var list = wow64 ? Run32List : RunList;
                foreach (var (name, command) in Read(() => system.ReadRunValues(scope, wow64)))
                {
                    if (!LaunchesSteam(command, steamExePath))
                    {
                        continue;
                    }

                    found.Add(new SteamAutostartSource(SteamAutostartKind.RunValue, scope,
                        (scope is SteamAutostartScope.User ? "HKCU" : "HKLM") + (wow64 ? " (32-bit)" : "") +
                        @"\...\Run",
                        name, IsApproved(system, scope, list, name), wow64));
                }
            }

            foreach (var (file, target) in Read(() => system.ReadStartupShortcuts(scope)))
            {
                if (!LaunchesSteam(target, steamExePath))
                {
                    continue;
                }

                found.Add(new SteamAutostartSource(SteamAutostartKind.StartupShortcut, scope,
                    scope is SteamAutostartScope.User ? "Startup folder" : "Common Startup folder",
                    file, IsApproved(system, scope, StartupFolderList, file)));
            }
        }

        foreach (var (path, command) in Read(system.ReadLogonTasks))
        {
            if (!LaunchesSteam(command, steamExePath))
            {
                continue;
            }

            // A task always needs elevation to change, whoever registered it.
            found.Add(new SteamAutostartSource(SteamAutostartKind.ScheduledTask, SteamAutostartScope.Machine,
                path, path, IsTaskEnabled(system, path)));
        }

        return
        [
            .. found.OrderBy(source => source.Kind).ThenBy(source => source.Location, StringComparer.OrdinalIgnoreCase)
                .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    ///     Whether a command line starts Steam. Compares the resolved executable rather than
    ///     the text, so an entry that merely mentions Steam in an argument is left alone.
    /// </summary>
    /// <param name="command">The stored command line or shortcut target.</param>
    /// <param name="steamExePath">Steam's own executable path, when it is known.</param>
    /// <returns>True when the first token is Steam's executable.</returns>
    private static bool LaunchesSteam(string? command, string? steamExePath)
    {
        var executable = FirstToken(command);
        if (executable is null)
        {
            return false;
        }

        if (!string.Equals(Path.GetFileName(executable), "steam.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(steamExePath))
        {
            // Without a known installation any steam.exe is the best evidence available.
            return true;
        }

        try
        {
            return string.Equals(Path.GetFullPath(executable), Path.GetFullPath(steamExePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Extracts the executable from a stored command line: environment variables expanded,
    ///     a quoted path taken whole, an unquoted one resolved the way Windows resolves it.
    /// </summary>
    /// <param name="command">The stored command line.</param>
    /// <returns>The executable path, or null when there is none.</returns>
    /// <remarks>
    ///     An unquoted path may contain spaces, and Windows itself tries successive prefixes rather
    ///     than stopping at the first one. Splitting at the first space would read
    ///     <c>C:\Program Files (x86)\Steam\steam.exe -silent</c> as <c>C:\Program</c> and miss the very
    ///     entry this exists to find.
    /// </remarks>
    private static string? FirstToken(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(command).Trim();
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            return closing > 1 ? text[1..closing] : null;
        }

        for (var space = text.IndexOf(' '); space > 0; space = text.IndexOf(' ', space + 1))
        {
            if (text[..space].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return text[..space];
            }
        }

        return text;
    }

    /// <summary>
    ///     Reads Windows' own enabled/disabled state. Byte zero carries the flag: an odd value
    ///     means the user or a tool disabled the entry. An absent value means Windows never stored a
    ///     decision, which is the enabled default.
    /// </summary>
    /// <param name="approval">The stored approval bytes, or null.</param>
    /// <returns>True when Windows would act on the entry.</returns>
    internal static bool ApprovalMeansEnabled(byte[]? approval)
    {
        return approval is not { Length: > 0 } || (approval[0] & 1) == 0;
    }

    private static bool IsApproved(IAutostartSystem system, SteamAutostartScope scope, string list, string name)
    {
        try
        {
            return ApprovalMeansEnabled(system.ReadApproval(scope, list, name));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Steam autostart: reading the approval for \"{name}\" failed: {ex.Message}");
            return true;
        }
    }

    private static bool IsTaskEnabled(IAutostartSystem system, string path)
    {
        try
        {
            return system.IsTaskEnabled(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Steam autostart: reading task \"{path}\" failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>One surface being unreadable must not hide the others.</summary>
    private static IReadOnlyDictionary<string, string> Read(Func<IReadOnlyDictionary<string, string>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Steam autostart: a startup surface could not be read: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
