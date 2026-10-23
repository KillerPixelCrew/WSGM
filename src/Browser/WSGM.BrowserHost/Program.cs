using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using WSGM.Interop;

namespace WSGM.BrowserHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Never fall back to an elevated renderer, including when UAC is disabled.
        if (args.Length != 4 || !args[0].StartsWith("WSGM.Browser.", StringComparison.Ordinal)
            || !int.TryParse(args[1], CultureInfo.InvariantCulture, out var parentId)
            || !long.TryParse(args[2], CultureInfo.InvariantCulture, out var parentStarted)
            || !BrowserNative.IsMediumIntegrity(Environment.ProcessId))
        {
            return 2;
        }

        try
        {
            var profile = Path.GetFullPath(args[3]);
            if (!Path.IsPathFullyQualified(args[3])
                || !string.Equals(Path.GetFileName(profile), "Browser", StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }

            using var parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != parentStarted
                || !string.Equals(parent.ProcessName, "WSGM", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            WindowsFormsSynchronizationContext.AutoInstall = true;
            using var window = new BrowserWindow(args[0], parent, profile);
            Application.Run(window);
            return 0;
        }
        catch
        {
            // The resident reports a missing/failed helper. No crash dialog may trap the game.
            return 1;
        }
    }
}
