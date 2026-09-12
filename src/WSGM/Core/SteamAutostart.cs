using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Where Windows can start Steam from without WSGM.</summary>
public enum SteamAutostartKind
{
    /// <summary>A value under a <c>CurrentVersion\Run</c> key.</summary>
    RunValue,
    /// <summary>A shortcut in a Startup folder.</summary>
    StartupShortcut,
    /// <summary>A scheduled task with a logon trigger.</summary>
    ScheduledTask,
}

/// <summary>Whether a source belongs to this user or to the machine.</summary>
public enum SteamAutostartScope
{
    /// <summary>Per-user; WSGM can change it without elevation.</summary>
    User,
    /// <summary>Machine-wide or a scheduled task; changing it needs elevation.</summary>
    Machine,
}

/// <summary>One place Windows starts Steam from.</summary>
/// <param name="Kind">What kind of source this is.</param>
/// <param name="Scope">Whether changing it needs elevation.</param>
/// <param name="Location">Registry key, folder or task path, for the log and the record.</param>
/// <param name="Name">The value name, file name or task name inside that location.</param>
/// <param name="Command">The command line or target the source launches.</param>
/// <param name="Enabled">Whether Windows would act on it as it stands.</param>
/// <param name="Wow64">Whether a run value lives in the 32-bit registry view, which Windows
/// approves through its own <c>Run32</c> list.</param>
public sealed record SteamAutostartSource(
    SteamAutostartKind Kind,
    SteamAutostartScope Scope,
    string Location,
    string Name,
    string Command,
    bool Enabled,
    bool Wow64 = false)
{
    /// <summary>Whether disabling this source needs an elevated process.</summary>
    public bool NeedsElevation => Scope is SteamAutostartScope.Machine;

    /// <summary>A short description for the Quick Setup list and the log.</summary>
    public string Describe() => Kind switch
    {
        SteamAutostartKind.RunValue => $"Startup entry \"{Name}\" ({Location})",
        SteamAutostartKind.StartupShortcut => $"Startup shortcut \"{Name}\"",
        _ => $"Scheduled task \"{Location}\"",
    };
}

/// <summary>Reads and writes the Windows startup surfaces. The seam exists so the matching and
/// takeover rules can be tested without touching this machine's registry or task scheduler.</summary>
public interface IAutostartSystem
{
    /// <summary>Reads the values of one <c>Run</c> key.</summary>
    /// <param name="scope">Which hive to read.</param>
    /// <param name="wow64">Whether to read the 32-bit view.</param>
    /// <returns>Value name to command line.</returns>
    IReadOnlyDictionary<string, string> ReadRunValues(SteamAutostartScope scope, bool wow64);

    /// <summary>Reads one <c>StartupApproved</c> state, or null when Windows has never stored one.</summary>
    /// <param name="scope">Which hive to read.</param>
    /// <param name="list">The approval list: <c>Run</c>, <c>Run32</c> or <c>StartupFolder</c>.</param>
    /// <param name="name">The value or file name.</param>
    /// <returns>The raw approval bytes, or null.</returns>
    byte[]? ReadApproval(SteamAutostartScope scope, string list, string name);

    /// <summary>Writes or removes one <c>StartupApproved</c> state.</summary>
    /// <param name="scope">Which hive to write.</param>
    /// <param name="list">The approval list.</param>
    /// <param name="name">The value or file name.</param>
    /// <param name="value">The bytes to write, or null to remove the value.</param>
    void WriteApproval(SteamAutostartScope scope, string list, string name, byte[]? value);

    /// <summary>Lists the shortcuts in one Startup folder with the target each resolves to.</summary>
    /// <param name="scope">Which Startup folder to read.</param>
    /// <returns>File name to resolved target path.</returns>
    IReadOnlyDictionary<string, string> ReadStartupShortcuts(SteamAutostartScope scope);

    /// <summary>Lists every scheduled task with a logon trigger, as task path to executed command.</summary>
    /// <returns>Task path to the command its action runs.</returns>
    IReadOnlyDictionary<string, string> ReadLogonTasks();

    /// <summary>Whether the named task is currently enabled.</summary>
    /// <param name="taskPath">The full task path.</param>
    /// <returns>True when Windows would run it.</returns>
    bool IsTaskEnabled(string taskPath);

    /// <summary>Enables or disables one scheduled task.</summary>
    /// <param name="taskPath">The full task path.</param>
    /// <param name="enabled">The state to set.</param>
    /// <returns>True when the change was accepted.</returns>
    bool SetTaskEnabled(string taskPath, bool enabled);
}

/// <summary>Finds the places Windows starts Steam from.
///
/// WSGM starts Steam itself so the client inherits WSGM's integrity, and a second Steam started by
/// Windows first would take that away silently. The matching is pure so it can be tested against
/// real command lines rather than this machine's own startup list.</summary>
public static class SteamAutostartScanner
{
    /// <summary>The StartupApproved list names Windows keeps per surface.</summary>
    internal const string RunList = "Run";
    internal const string Run32List = "Run32";
    internal const string StartupFolderList = "StartupFolder";

    /// <summary>Finds every startup source that launches Steam.</summary>
    /// <param name="system">The startup surfaces to read.</param>
    /// <param name="steamExePath">Steam's own executable path, or null when Steam is not installed.</param>
    /// <returns>The matching sources, ordered so the log reads the same way twice.</returns>
    public static IReadOnlyList<SteamAutostartSource> Scan(IAutostartSystem system, string? steamExePath)
    {
        ArgumentNullException.ThrowIfNull(system);
        List<SteamAutostartSource> found = [];
        foreach (SteamAutostartScope scope in new[] { SteamAutostartScope.User, SteamAutostartScope.Machine })
        {
            foreach (bool wow64 in new[] { false, true })
            {
                string list = wow64 ? Run32List : RunList;
                foreach ((string name, string command) in Read(() => system.ReadRunValues(scope, wow64)))
                {
                    if (!LaunchesSteam(command, steamExePath)) { continue; }
                    found.Add(new(SteamAutostartKind.RunValue, scope,
                        (scope is SteamAutostartScope.User ? "HKCU" : "HKLM") + (wow64 ? " (32-bit)" : "") + @"\...\Run",
                        name, command, IsApproved(system, scope, list, name), wow64));
                }
            }

            foreach ((string file, string target) in Read(() => system.ReadStartupShortcuts(scope)))
            {
                if (!LaunchesSteam(target, steamExePath)) { continue; }
                found.Add(new(SteamAutostartKind.StartupShortcut, scope,
                    scope is SteamAutostartScope.User ? "Startup folder" : "Common Startup folder",
                    file, target, IsApproved(system, scope, StartupFolderList, file)));
            }
        }

        foreach ((string path, string command) in Read(system.ReadLogonTasks))
        {
            if (!LaunchesSteam(command, steamExePath)) { continue; }
            // A task always needs elevation to change, whoever registered it.
            found.Add(new(SteamAutostartKind.ScheduledTask, SteamAutostartScope.Machine,
                path, path, command, IsTaskEnabled(system, path)));
        }

        return [.. found.OrderBy(source => source.Kind).ThenBy(source => source.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Whether a command line starts Steam. Compares the resolved executable rather than
    /// the text, so an entry that merely mentions Steam in an argument is left alone.</summary>
    /// <param name="command">The stored command line or shortcut target.</param>
    /// <param name="steamExePath">Steam's own executable path, when it is known.</param>
    /// <returns>True when the first token is Steam's executable.</returns>
    internal static bool LaunchesSteam(string? command, string? steamExePath)
    {
        string? executable = FirstToken(command);
        if (executable is null) { return false; }
        if (!executable.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase)) { return false; }
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

    /// <summary>Extracts the executable from a stored command line: environment variables expanded,
    /// a quoted path taken whole, an unquoted one resolved the way Windows resolves it.</summary>
    /// <param name="command">The stored command line.</param>
    /// <returns>The executable path, or null when there is none.</returns>
    /// <remarks>
    /// An unquoted path may contain spaces, and Windows itself tries successive prefixes rather
    /// than stopping at the first one. Splitting at the first space would read
    /// <c>C:\Program Files (x86)\Steam\steam.exe -silent</c> as <c>C:\Program</c> and miss the very
    /// entry this exists to find.
    /// </remarks>
    internal static string? FirstToken(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) { return null; }
        string text = Environment.ExpandEnvironmentVariables(command).Trim();
        if (text.StartsWith('"'))
        {
            int closing = text.IndexOf('"', 1);
            return closing > 1 ? text[1..closing] : null;
        }
        for (int space = text.IndexOf(' '); space > 0; space = text.IndexOf(' ', space + 1))
        {
            if (text[..space].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { return text[..space]; }
        }
        return text;
    }

    /// <summary>Reads Windows' own enabled/disabled state. Byte zero carries the flag: an odd value
    /// means the user or a tool disabled the entry. An absent value means Windows never stored a
    /// decision, which is the enabled default.</summary>
    /// <param name="approval">The stored approval bytes, or null.</param>
    /// <returns>True when Windows would act on the entry.</returns>
    internal static bool ApprovalMeansEnabled(byte[]? approval) =>
        approval is not { Length: > 0 } || (approval[0] & 1) == 0;

    private static bool IsApproved(IAutostartSystem system, SteamAutostartScope scope, string list, string name)
    {
        try { return ApprovalMeansEnabled(system.ReadApproval(scope, list, name)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Steam autostart: reading the approval for \"{name}\" failed: {ex.Message}");
            return true;
        }
    }

    private static bool IsTaskEnabled(IAutostartSystem system, string path)
    {
        try { return system.IsTaskEnabled(path); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Steam autostart: reading task \"{path}\" failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>One surface being unreadable must not hide the others.</summary>
    private static IReadOnlyDictionary<string, string> Read(Func<IReadOnlyDictionary<string, string>> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Steam autostart: a startup surface could not be read: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
