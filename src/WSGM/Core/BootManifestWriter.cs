using System;
using System.IO;

namespace WSGM.Core;

/// <summary>
///     Projects the current config into boot.json for the logon service
///     (WSGM-side only — the shared contract lives in Core\BootManifest). Called on
///     --setup, on settings saves that touch the inputs, and at every shell/boot
///     start so a stale Elevate/ExePath heals itself on the next session.
/// </summary>
public static class BootManifestWriter
{
    /// <summary>
    ///     Writes boot.json from <paramref name="config" />. Best effort: a
    ///     failed write logs and returns false. An older manifest may still request startup;
    ///     callers that report a saved startup preference must report this failure too.
    /// </summary>
    /// <param name="context">The explicit directory receiving the boot manifest.</param>
    public static bool WriteCurrent(AppConfig config, UserDataContext context)
    {
        try
        {
            var manifest = new BootManifest
            {
                GameModeBoot = config is { StartAtSignIn: true, StartMode: SessionStartMode.Game },
                DesktopResident = config is { StartAtSignIn: true, StartMode: SessionStartMode.Desktop },
                Elevate = ElevationPolicy.WantsElevation(config, Steam.RequiresElevatedShell),
                // WSGM runs from where setup installed it, so the running image is the installed one.
                ExePath = Installer.InstalledExePath
            };
            BootManifestStore.Save(Path.Combine(context.Root, BootManifestStore.FileName), manifest);
            Log.Info($"Boot manifest written: game={manifest.GameModeBoot} desktop={manifest.DesktopResident} "
                     + $"elevate={manifest.Elevate} exe={manifest.ExePath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Boot manifest write failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Rewrites boot.json with the sign-in start disabled. Used by the crash-loop breaker
    ///     and the restore-shell escape hatch so the next sign-in is a plain Windows desktop even when
    ///     config.json cannot be saved.
    /// </summary>
    /// <param name="config">The configuration to disarm and project.</param>
    /// <param name="context">The explicit directory receiving the boot manifest.</param>
    public static bool WriteSignInDisabled(AppConfig config, UserDataContext context)
    {
        config.StartAtSignIn = false;
        return WriteCurrent(config, context);
    }
}
