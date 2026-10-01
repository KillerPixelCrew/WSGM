using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>One program in Windows' installed-programs list.</summary>
/// <param name="KeyName">The registry key's own name, which some launchers use as an id.</param>
/// <param name="DisplayName">What Windows calls it.</param>
/// <param name="InstallLocation">Where it says it is installed, or empty.</param>
/// <param name="Publisher">Who published it, or empty.</param>
/// <param name="UninstallString">The command that removes it, or empty.</param>
/// <param name="DisplayIcon">Its icon, often the main executable, or empty.</param>
public sealed record UninstallEntry(
    string KeyName,
    string DisplayName,
    string InstallLocation,
    string Publisher,
    string UninstallString,
    string DisplayIcon);

/// <summary>Reads Windows' installed-programs list, the way launchers' installers register themselves.</summary>
/// <remarks>
///     <para>
///         Both hives and both registry views, as Playnite reads them: a 32-bit launcher registers under
///         <c>WOW6432Node</c>, and a per-user install under the current user. The same key found twice is
///         reported once, from the first place it was found.
///     </para>
///     <para>
///         A Game Library scan reads the list once and hands the same entries to every launcher source,
///         which detect their launcher and discover their games from it. Nothing is kept between calls,
///         so an install or uninstall shows at the very next scan.
///     </para>
///     <para>
///         Each key is read inside its own try, so one key the process may not open drops that entry
///         alone rather than every entry after it in the same view.
///     </para>
/// </remarks>
public static class UninstallEntries
{
    private const string Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Every entry that has a display name, read from the registry on every call.</summary>
    /// <returns>The entries, machine-wide first.</returns>
    public static IReadOnlyList<UninstallEntry> Read()
    {
        List<UninstallEntry> entries = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                     (RegistryHive.CurrentUser, RegistryView.Registry32)
                 })
        {
            string[] names;
            RegistryKey? uninstall;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                uninstall = root.OpenSubKey(Path);
                if (uninstall is null)
                {
                    continue;
                }

                names = uninstall.GetSubKeyNames();
            }
            catch (Exception ex) when (IsUnreadable(ex))
            {
                // A view the process cannot read has nothing in it for this purpose.
                continue;
            }

            using (uninstall)
            {
                foreach (var name in names)
                {
                    if (ReadEntry(uninstall, name) is { } entry && seen.Add(name))
                    {
                        entries.Add(entry);
                    }
                }
            }
        }

        return entries;
    }

    /// <summary>Finds a program an installed-programs entry says it installed.</summary>
    /// <param name="entries">The entries to search.</param>
    /// <param name="match">Whether an entry is the one wanted, such as by its display name.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="executables">The program's file names, relative to the install location, in preference order.</param>
    /// <returns>The first program that exists under a matching entry's install location, or null.</returns>
    public static string? FindProgram(
        IReadOnlyList<UninstallEntry> entries,
        Func<UninstallEntry, bool> match,
        Func<string, bool> fileExists,
        params string[] executables)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(executables);
        foreach (var entry in entries)
        {
            if (entry.InstallLocation.Length == 0 || !match(entry))
            {
                continue;
            }

            foreach (var executable in executables)
            {
                var program = System.IO.Path.Combine(entry.InstallLocation, executable);
                if (fileExists(program))
                {
                    return program;
                }
            }
        }

        return null;
    }

    private static UninstallEntry? ReadEntry(RegistryKey uninstall, string name)
    {
        try
        {
            using var key = uninstall.OpenSubKey(name);
            if (key?.GetValue("DisplayName") is not string { Length: > 0 } displayName)
            {
                return null;
            }

            return new UninstallEntry(
                name,
                displayName,
                Text(key, "InstallLocation").Trim('"'),
                Text(key, "Publisher"),
                Text(key, "UninstallString"),
                Text(key, "DisplayIcon"));
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            // One key the process may not open is one program fewer, not a view fewer.
            return null;
        }
    }

    private static bool IsUnreadable(Exception ex)
    {
        return ex is SecurityException or UnauthorizedAccessException or IOException;
    }

    private static string Text(RegistryKey key, string name)
    {
        return key.GetValue(name) as string ?? string.Empty;
    }
}
