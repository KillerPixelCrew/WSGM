using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>Turns what Windows lists in Start into the applications worth offering as a Program.</summary>
internal static partial class InstalledProgramCatalog
{
    /// <summary>Keeps launchable applications, drops duplicates and sorts by name.</summary>
    /// <param name="listed">The Applications folder's entries.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <returns>The offered applications, sorted by name.</returns>
    /// <remarks>
    ///     A packaged entry is listed only when its package declares it for Start, so it is kept as it is. A
    ///     desktop entry is any Start Menu shortcut, which is where uninstallers, readmes and website links live:
    ///     it has to start an existing executable and not be maintenance tooling.
    /// </remarks>
    internal static IReadOnlyList<InstalledProgram> Merge(IEnumerable<InstalledApplication> listed,
        Func<string, bool> fileExists)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<InstalledProgram> programs = [];
        foreach (var application in listed)
        {
            var name = application.DisplayName.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (application.AppUserModelId is { Length: > 0 } aumid)
            {
                if (seen.Add(aumid))
                {
                    programs.Add(new InstalledProgram(application.ParsingName, name, aumid, null, ""));
                }

                continue;
            }

            if (application.TargetPath is not { Length: > 0 } target || !Launchable(name, target, fileExists))
            {
                continue;
            }

            var arguments = application.Arguments.Trim();
            if (seen.Add(target + "|" + arguments))
            {
                programs.Add(new InstalledProgram(application.ParsingName, name, null, target, arguments));
            }
        }

        return programs.OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool Launchable(string name, string target, Func<string, bool> fileExists)
    {
        var file = Path.GetFileName(target);
        return string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
               // WSGM owns the shell in Game Mode; starting Explorer as a tool would bring the desktop shell up.
               && !string.Equals(file, "explorer.exe", StringComparison.OrdinalIgnoreCase)
               && !MaintenanceName().IsMatch(name)
               && !MaintenanceFile().IsMatch(file)
               && fileExists(target);
    }

    [GeneratedRegex(
        @"\b(uninstall|uninstaller|deinstall|deinstallieren|remove|repair|setup|readme|read me|release notes|documentation|website|license|changelog|help)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaintenanceName();

    [GeneratedRegex(@"^(unins|uninstall|setup\.exe$|install|crashreport)|helper",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaintenanceFile();
}
