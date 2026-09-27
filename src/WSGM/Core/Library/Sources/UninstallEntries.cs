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
///     Both hives and both registry views, as Playnite reads them: a 32-bit launcher registers under
///     <c>WOW6432Node</c>, and a per-user install under the current user. The same key found twice is
///     reported once, from the first place it was found.
/// </remarks>
public static class UninstallEntries
{
    private const string Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Every entry that has a display name.</summary>
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
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(Path);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string { Length: > 0 } displayName
                        || !seen.Add(name))
                    {
                        continue;
                    }

                    entries.Add(new UninstallEntry(
                        name,
                        displayName,
                        Text(key, "InstallLocation").Trim('"'),
                        Text(key, "Publisher"),
                        Text(key, "UninstallString"),
                        Text(key, "DisplayIcon")));
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException
                                           or IOException)
            {
                // A view the process cannot read has nothing in it for this purpose.
            }
        }

        return entries;
    }

    private static string Text(RegistryKey key, string name)
    {
        return key.GetValue(name) as string ?? string.Empty;
    }
}
