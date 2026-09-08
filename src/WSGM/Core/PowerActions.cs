using System;
using System.Threading.Tasks;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>Logs explicit user power intent and observes the shared Windows backend.</summary>
public static class PowerActions
{
    /// <summary>Requests standby without blocking the UI; Windows completes the call after resume.</summary>
    public static void Standby() => Observe(() => WindowsPower.SuspendAsync(false), "standby");

    /// <summary>Requests hibernation without blocking the UI.</summary>
    public static void Hibernate() => Observe(() => WindowsPower.SuspendAsync(true), "hibernate");

    /// <summary>Requests a Windows restart.</summary>
    public static void Restart() => Observe(() => WindowsPower.RequestActionAsync(WindowsPowerAction.Restart), "restart");

    /// <summary>Requests a Windows shutdown.</summary>
    public static void Shutdown() => Observe(() => WindowsPower.RequestActionAsync(WindowsPowerAction.Shutdown), "shutdown");

    private static void Observe(Func<Task> request, string operation) => _ = ObserveAsync(request, operation);

    private static async Task ObserveAsync(Func<Task> request, string operation)
    {
        Log.Info($"Power: {operation}");
        try { await request().ConfigureAwait(false); }
        catch (Exception ex) { Log.Error($"Power: {operation} failed", ex); }
    }
}
