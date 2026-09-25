using System;
using System.IO;

namespace WSGM.Core;

/// <summary>
///     Install-lifecycle helpers behind setup: where the running application lives and the
///     machine-setting rollback the uninstaller drives through <c>--uninstall-restore</c>.
/// </summary>
public static class Installer
{
    /// <summary>Gets the directory of the running application.</summary>
    /// <remarks>
    ///     Setup installs to <c>%ProgramFiles%\WSGM\App</c> (<see cref="WSGM.Install.InstallLayout.App" />)
    ///     and runs <c>--setup</c> from there, so everything recorded from this path (the boot manifest,
    ///     Steam launch options, generated shortcuts) names the installed copy. A development deploy
    ///     that runs from another folder records that folder instead.
    /// </remarks>
    public static string InstallDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>Gets the running WSGM executable path.</summary>
    public static string InstalledExePath => Path.Combine(InstallDir, "WSGM.exe");

    /// <summary>
    ///     Best-effort rollback of every machine/user setting WSGM changed
    ///     outside its own directory: display scaling, UAC prompt level, and
    ///     lock-on-wake. Called by --uninstall-restore, which the elevated
    ///     uninstaller runs so the HKLM writes succeed directly; each step is isolated
    ///     so one failure cannot stop the rest.
    /// </summary>
    public static void RestoreMachineSettings()
    {
        try
        {
            var config = ConfigStore.Load();
            DisplayScale.RestoreSaved(config);
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: display scaling failed: {ex.Message}");
        }

        try
        {
            var config = ConfigStore.Load();
            if (config.PreviousUacSnapshotCaptured && UacSettings.Read().PromptsDisabled)
            {
                Log.Info("Uninstall restore: restoring UAC prompt level.");
                UacSettings.ApplyDirect(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: UAC failed: {ex.Message}");
        }

        try
        {
            // Steam has to start the way it did before WSGM took that over.
            SteamAutostartService.RestoreAll();
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: Steam autostart failed: {ex.Message}");
        }

        // Handheld Companion and the maker's apps start again the way they did before setup's Full mode.
        OtherManagers.RestoreAll();

        try
        {
            var config = ConfigStore.Load();
            if (!config.PreviousLockOnWakeSnapshotCaptured || !LockScreenSettings.SignInOnWakeDisabled())
            {
                return;
            }

            Log.Info("Uninstall restore: restoring lock-on-wake.");
            LockScreenSettings.ApplyDirect(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: lock-on-wake failed: {ex.Message}");
        }
    }
}
