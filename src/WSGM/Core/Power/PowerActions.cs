using System;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Logs explicit user power intent and observes the shared Windows backend.</summary>
public static class PowerActions
{
    private static readonly Lazy<bool> ModernStandbyOnly = new(ReadModernStandby);

    /// <summary>Requests the action without blocking the UI; a suspend completes after resume.</summary>
    /// <param name="action">The action the user confirmed.</param>
    public static void Request(SessionPowerAction action)
    {
        _ = action switch
        {
            SessionPowerAction.Standby => StandbyAsync(),
            SessionPowerAction.Hibernate => ObserveAsync(() => WindowsPower.SuspendAsync(true), "hibernate"),
            SessionPowerAction.Restart => ObserveAsync(
                () => WindowsPower.RequestActionAsync(WindowsPowerAction.Restart), "restart"),
            SessionPowerAction.Shutdown => ObserveAsync(
                () => WindowsPower.RequestActionAsync(WindowsPowerAction.Shutdown), "shutdown"),
            SessionPowerAction.SignOut => ObserveAsync(
                () => WindowsPower.RequestActionAsync(WindowsPowerAction.SignOut), "sign out"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    /// <summary>Puts the machine to sleep the way its own Sleep does.</summary>
    /// <returns>Whether the request was accepted; a classic suspend reports true once requested.</returns>
    /// <remarks>
    ///     A Modern Standby (S0 low power idle) machine has no S3, and Windows turns SetSuspendState's
    ///     standby into hibernation there (Kernel-Power 187, then a firmware boot; Atlas 8, 2026-11-01).
    ///     Turning the display off is what enters Modern Standby on such a machine, as the display timeout
    ///     does. Every other machine keeps the classic suspend.
    /// </remarks>
    public static bool EnterStandby()
    {
        if (!ModernStandbyOnly.Value)
        {
            _ = ObserveAsync(() => WindowsPower.SuspendAsync(false), "standby");
            return true;
        }

        Log.Info("Power: standby (Modern Standby)");
        if (MonitorPower.TurnOff())
        {
            return true;
        }

        Log.Error("Power: standby (Modern Standby) was not accepted by Windows.");
        return false;
    }

    private static Task StandbyAsync()
    {
        EnterStandby();
        return Task.CompletedTask;
    }

    private static bool ReadModernStandby()
    {
        try
        {
            return ModernStandby.Query().LowPowerIdle;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Power: Modern Standby support could not be read, using the classic suspend: {ex.Message}");
            return false;
        }
    }

    private static async Task ObserveAsync(Func<Task> request, string operation)
    {
        Log.Info($"Power: {operation}");
        try
        {
            await request().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"Power: {operation} failed", ex);
        }
    }
}
