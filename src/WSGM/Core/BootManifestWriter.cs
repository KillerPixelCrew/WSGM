using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Projects the current config into boot.json for the logon service
/// (WSGM-side only — the shared contract lives in Core\BootManifest). Called on
/// --setup, on settings saves that touch the inputs, and at every shell/boot
/// start so a stale Elevate/ExePath heals itself on the next session.</summary>
public static class BootManifestWriter
{
    /// <summary>Absolute path of the per-user boot manifest.</summary>
    public static string ManifestPath => Path.Combine(Log.Directory, BootManifestStore.FileName);

    /// <summary>Writes boot.json from <paramref name="config"/>. Best effort: a
    /// failed write only logs — the service then skips the next logon, which is
    /// recoverable, unlike a crashed setup/boot path.</summary>
    public static void WriteCurrent(AppConfig config)
    {
        try
        {
            var manifest = new BootManifest
            {
                GameModeBoot = config.StartAtSignIn && config.StartMode is SessionStartMode.Game,
                DesktopResident = config.StartAtSignIn && config.StartMode is SessionStartMode.Desktop,
                Elevate = ElevationPolicy.WantsElevation(config, Steam.RequiresElevatedShell),
                // Inno is the only installer, so the installed path is the only path.
                ExePath = Installer.InstalledExePath,
            };
            BootManifestStore.Save(ManifestPath, manifest);
            Log.Info($"Boot manifest written: game={manifest.GameModeBoot} desktop={manifest.DesktopResident} "
                + $"elevate={manifest.Elevate} exe={manifest.ExePath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Boot manifest write failed: {ex.Message}");
        }
    }

    /// <summary>Rewrites boot.json with the sign-in start disabled. Used by the crash-loop breaker
    /// and the restore-shell escape hatch so the next sign-in is a plain Windows desktop even when
    /// config.json cannot be saved.</summary>
    /// <param name="config">The configuration to disarm and project.</param>
    public static void WriteSignInDisabled(AppConfig config)
    {
        config.StartAtSignIn = false;
        WriteCurrent(config);
    }
}
