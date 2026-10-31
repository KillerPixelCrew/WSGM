using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    /// <summary>Puts packages a killed launcher left exempt back under lifetime management.</summary>
    /// <remarks>
    ///     <para>
    ///         Steam's Stop button terminates the launcher outright, so its own release never runs
    ///         and the package stays exempt. Nothing else ever puts it back: the next launcher only
    ///         sweeps when the user happens to start another imported game, so without this a single
    ///         Stop leaves a package outside lifetime management for the life of the machine.
    ///     </para>
    ///     <para>
    ///         The launcher owns the COM interop for this, so it does the work and WSGM just asks.
    ///         Fire and forget, off the startup path: a sweep that cannot run is not a reason to
    ///         hold up a session.
    ///     </para>
    /// </remarks>
    private static void ReleaseAbandonedPackageExemptions()
    {
        if (PackagedLauncherShortcut.ResolveLauncher() is not { } launcher)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(launcher, "--recover")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                           or ObjectDisposedException or IOException)
            {
                Log.Warn($"Packaged launcher recovery sweep did not run: {ex.Message}");
            }
        });
    }
}
