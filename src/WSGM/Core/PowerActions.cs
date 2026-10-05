using System;
using System.Threading.Tasks;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>A machine power action the user asked the session for.</summary>
public enum SessionPowerAction
{
    /// <summary>Enter standby.</summary>
    Standby,

    /// <summary>Hibernate.</summary>
    Hibernate,

    /// <summary>Restart Windows.</summary>
    Restart,

    /// <summary>Shut Windows down.</summary>
    Shutdown,

    /// <summary>Sign the current Windows user out.</summary>
    SignOut
}

/// <summary>Logs explicit user power intent and observes the shared Windows backend.</summary>
public static class PowerActions
{
    /// <summary>Requests the action without blocking the UI; a suspend completes after resume.</summary>
    /// <param name="action">The action the user confirmed.</param>
    public static void Request(SessionPowerAction action)
    {
        _ = action switch
        {
            SessionPowerAction.Standby => ObserveAsync(() => WindowsPower.SuspendAsync(false), "standby"),
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
