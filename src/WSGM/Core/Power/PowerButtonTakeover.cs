using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     Holds Windows' own power-button handling off while WSGM handles the button, and puts it back.
/// </summary>
/// <remarks>
///     Two things are changed. Every power plan's power-button action (PBUTTONACTION) is set to "Do
///     nothing", and SlideToShutDown.exe, which winlogon starts on its own when the button is held on a
///     touch-screen PC whatever that action says, is redirected through Image File Execution Options to
///     <c>WSGM.exe --slide-blocked</c>, which exits at once. The originals are saved to the configuration
///     before anything is written, so a crash leaves them for restore-shell, panic recovery, the next
///     start and uninstall. Restoring writes every saved value once and keeps the saved state when any
///     step fails. The sign-in screen has no WSGM, so a session that ends restores Windows' handling.
/// </remarks>
internal static class PowerButtonTakeover
{
    /// <summary>The command-line flag Windows passes to WSGM in place of SlideToShutDown.exe.</summary>
    internal const string SlideBlockedFlag = "--slide-blocked";

    private const string SlideKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\SlideToShutDown.exe";

    private static readonly Guid ButtonsSubgroup = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid PowerButtonAction = new("7648efa3-dd9c-4e3e-b566-50f929386280");

    /// <summary>Whether a takeover is recorded and may still be in force.</summary>
    /// <param name="config">The configuration to inspect.</param>
    /// <returns>True when restoring has something to do.</returns>
    internal static bool IsRecorded(AppConfig config)
    {
        return config.PreviousPowerButtonActions.Count > 0 || config.PowerButtonSlideBlocked;
    }

    /// <summary>Saves the current actions, then sets every plan to "Do nothing" and blocks the slide screen.</summary>
    /// <param name="store">The configuration the originals are saved to first.</param>
    /// <returns>False when Windows could not be changed; the saved originals then still restore.</returns>
    internal static bool TakeOver(ConfigStore store)
    {
        try
        {
            using var operation = new WindowsPolicyOperation("PowerButton");
            var plans = Plans();
            var current = plans.Select(plan => new PowerSchemeButtonAction
            {
                SchemeGuid = plan.ToString("D"),
                AcValue = WindowsPower.ReadSetting(plan, ButtonsSubgroup, PowerButtonAction, false),
                DcValue = WindowsPower.ReadSetting(plan, ButtonsSubgroup, PowerButtonAction, true)
            }).ToList();
            var command = SlideCommand();
            store.Update(fresh =>
            {
                // A plan already saved keeps its first original: after a crash it reads "Do nothing" now.
                foreach (var plan in current.Where(plan => plan.AcValue != 0 || plan.DcValue != 0))
                {
                    fresh.PreviousPowerButtonActions.RemoveAll(saved => saved.SchemeGuid == plan.SchemeGuid);
                    fresh.PreviousPowerButtonActions.Add(plan);
                }

                fresh.PowerButtonSlideBlocked |= command is not null
                                                 && (SlideRedirect() is not { } existing || IsOurs(existing));
                return true;
            });

            foreach (var plan in plans)
            {
                WindowsPower.WriteSetting(plan, ButtonsSubgroup, PowerButtonAction, false, 0);
                WindowsPower.WriteSetting(plan, ButtonsSubgroup, PowerButtonAction, true, 0);
            }

            WindowsPower.RefreshActiveScheme();
            BlockSlide(command);
            Log.Info($"Power button: Windows' own handling is off in {plans.Count} power plan(s).");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Power button: Windows' own handling could not be switched off", ex);
            return false;
        }
    }

    /// <summary>Puts back every saved action and removes WSGM's slide redirect.</summary>
    /// <param name="store">The configuration holding the originals.</param>
    /// <returns>True when nothing is left to restore.</returns>
    internal static bool Restore(ConfigStore store)
    {
        try
        {
            using var operation = new WindowsPolicyOperation("PowerButton");
            var saved = store.Read().RequireConfig();
            if (!IsRecorded(saved))
            {
                return true;
            }

            List<string> failures = [];
            var plans = Plans();
            foreach (var plan in saved.PreviousPowerButtonActions)
            {
                if (!Guid.TryParse(plan.SchemeGuid, out var id) || !plans.Contains(id))
                {
                    continue;
                }

                try
                {
                    WindowsPower.WriteSetting(id, ButtonsSubgroup, PowerButtonAction, false, plan.AcValue);
                    WindowsPower.WriteSetting(id, ButtonsSubgroup, PowerButtonAction, true, plan.DcValue);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add($"plan {plan.SchemeGuid}: {ex.Message}");
                }
            }

            try
            {
                WindowsPower.RefreshActiveScheme();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"active plan refresh: {ex.Message}");
            }

            if (saved.PowerButtonSlideBlocked && !UnblockSlide(out var slideError))
            {
                failures.Add($"slide redirect: {slideError}");
            }

            if (failures.Count > 0)
            {
                foreach (var failure in failures)
                {
                    Log.Warn($"Power button restore: {failure}");
                }

                Log.Error($"Power button: Windows' own handling was not fully restored ({failures.Count} step(s)).");
                return false;
            }

            store.Update(fresh =>
            {
                fresh.PreviousPowerButtonActions = [];
                fresh.PowerButtonSlideBlocked = false;
                return true;
            });
            Log.Info("Power button: Windows' own handling restored.");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Power button: Windows' own handling could not be restored", ex);
            return false;
        }
    }

    /// <summary>Restore-shell, panic, start-up and uninstall recovery; never throws.</summary>
    /// <param name="store">The configuration holding the originals.</param>
    internal static void RestoreBestEffort(ConfigStore store)
    {
        try
        {
            var read = store.Read();
            if (read.Config is { } config && IsRecorded(config))
            {
                Restore(store);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Power button recovery remains pending: {ex.Message}");
        }
    }

    private static IReadOnlyList<Guid> Plans()
    {
        var plans = WindowsPower.EnumerateSchemes();
        return plans.Count > 0 ? plans : [WindowsPower.GetActiveScheme()];
    }

    /// <summary>The redirect WSGM writes, or null when the running executable is unknown.</summary>
    private static string? SlideCommand()
    {
        return Environment.ProcessPath is { Length: > 0 } path ? $"\"{path}\" {SlideBlockedFlag}" : null;
    }

    /// <summary>The Debugger value currently registered for SlideToShutDown.exe, or null.</summary>
    private static string? SlideRedirect()
    {
        using var key = Registry.LocalMachine.OpenSubKey(SlideKey);
        return key?.GetValue("Debugger") as string;
    }

    private static void BlockSlide(string? command)
    {
        if (command is null)
        {
            return;
        }

        try
        {
            var existing = SlideRedirect();
            if (existing is not null && !IsOurs(existing))
            {
                // Another program owns this redirect; it is never overwritten or removed.
                Log.Warn($"Power button: SlideToShutDown.exe is already redirected by another program ({existing}).");
                return;
            }

            using var key = Registry.LocalMachine.CreateSubKey(SlideKey);
            key.SetValue("Debugger", command);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or System.IO.IOException)
        {
            Log.Warn($"Power button: the slide-to-shut-down screen could not be blocked: {ex.Message}");
        }
    }

    private static bool UnblockSlide(out string error)
    {
        error = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SlideKey, true);
            if (key?.GetValue("Debugger") is not string existing || !IsOurs(existing))
            {
                return true;
            }

            key.DeleteValue("Debugger", false);
            var empty = key.ValueCount == 0 && key.SubKeyCount == 0;
            key.Close();
            if (empty)
            {
                Registry.LocalMachine.DeleteSubKey(SlideKey, false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or System.IO.IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Whether a redirect is WSGM's own, from this or an earlier install location.</summary>
    private static bool IsOurs(string command)
    {
        return command.EndsWith(SlideBlockedFlag, StringComparison.OrdinalIgnoreCase)
               && command.Contains("WSGM", StringComparison.OrdinalIgnoreCase);
    }
}
