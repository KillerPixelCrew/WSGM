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
    ///     Writes boot.json only from a Loaded or Absent configuration read. Best effort: a
    ///     failed write logs and returns false. An older manifest may still request startup;
    ///     callers that report a saved startup preference must report this failure too.
    /// </summary>
    /// <param name="context">The explicit directory receiving the boot manifest.</param>
    /// <param name="read">The read that supplies the projection.</param>
    public static bool WriteCurrent(ConfigReadResult read, UserDataContext context)
    {
        return (read.Outcome is ConfigReadOutcome.Loaded or ConfigReadOutcome.Absent)
               && read.Config is { } config && WriteProjection(config, context, config.StartAtSignIn);
    }

    private static bool WriteProjection(AppConfig config, UserDataContext context, bool startAtSignIn)
    {
        try
        {
            var manifest = new BootManifest
            {
                GameModeBoot = startAtSignIn && config.StartMode == SessionStartMode.Game,
                DesktopResident = startAtSignIn && config.StartMode == SessionStartMode.Desktop,
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
    /// <param name="config">The configuration used for elevation; it is not modified.</param>
    /// <param name="context">The explicit directory receiving the boot manifest.</param>
    public static bool WriteSignInDisabled(AppConfig config, UserDataContext context)
    {
        return WriteProjection(config, context, startAtSignIn: false);
    }
}
