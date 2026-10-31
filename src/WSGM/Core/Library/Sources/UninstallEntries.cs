using System;
using System.Collections.Generic;
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
        return SubKeys(
            [
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
                (RegistryHive.CurrentUser, RegistryView.Registry32)
            ],
            Path,
            ReadEntry);
    }

    /// <summary>Reads every subkey of one registry key across several hives and views.</summary>
    /// <param name="places">The hives and views to look in, in preference order.</param>
    /// <param name="path">The key whose subkeys are read.</param>
    /// <param name="read">Reads one opened subkey and its name, or returns null to skip it.</param>
    /// <returns>One value per subkey name, from the first place that yielded one.</returns>
    /// <remarks>
    ///     A view the process cannot read lists nothing, and one subkey it may not open is one value
    ///     fewer, not a view fewer.
    /// </remarks>
    internal static List<T> SubKeys<T>(
        IEnumerable<(RegistryHive Hive, RegistryView View)> places,
        string path,
        Func<RegistryKey, string, T?> read)
        where T : class
    {
        List<T> values = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in places)
        {
            string[] names;
            RegistryKey? parent;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                parent = root.OpenSubKey(path);
                if (parent is null)
                {
                    continue;
                }

                names = parent.GetSubKeyNames();
            }
            catch (Exception ex) when (LibraryFiles.IsUnreadable(ex))
            {
                continue;
            }

            using (parent)
            {
                foreach (var name in names)
                {
                    try
                    {
                        using var key = parent.OpenSubKey(name);
                        if (key is not null && read(key, name) is { } value && seen.Add(name))
                        {
                            values.Add(value);
                        }
                    }
                    catch (Exception ex) when (LibraryFiles.IsUnreadable(ex))
                    {
                        Log.Debug($"Registry key {path}\\{name} could not be read: {ex.Message}");
                    }
                }
            }
        }

        return values;
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

    private static UninstallEntry? ReadEntry(RegistryKey key, string name)
    {
        if (key.GetValue("DisplayName") is not string { Length: > 0 } displayName)
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

    private static string Text(RegistryKey key, string name)
    {
        return key.GetValue(name) as string ?? string.Empty;
    }
}
