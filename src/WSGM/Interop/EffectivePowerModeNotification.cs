using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.Core;

namespace WSGM.Interop;

/// <summary>
///     A registration for Windows' effective power mode notifications
///     (<c>PowerRegisterForEffectivePowerModeNotifications</c>).
/// </summary>
/// <remarks>
///     The effective mode is what <c>PowerGetEffectiveOverlayScheme</c> reads, so the callback fires when the
///     Windows power mode slider moves, whoever moved it. Windows calls it on a thread-pool thread, once
///     right after registration with the current mode and then on every change. The callback receives a
///     registration id rather than a pointer to this object, so a notification that races
///     <see cref="Dispose" /> finds nothing to call instead of a freed handle.
/// </remarks>
internal sealed unsafe partial class EffectivePowerModeNotification : IDisposable
{
    /// <summary>EFFECTIVE_POWER_MODE_V2, the version that reports the Windows 11 modes.</summary>
    private const uint EffectivePowerModeV2 = 2;

    private static readonly Dictionary<nint, Action> Callbacks = [];
    private static readonly Lock Gate = new();
    private static nint _lastId;
    private readonly nint _id;
    private nint _registration;

    private EffectivePowerModeNotification(nint id, nint registration)
    {
        _id = id;
        _registration = registration;
    }

    /// <summary>Stops the notifications. A callback already running may still complete; none starts after this.</summary>
    public void Dispose()
    {
        var registration = Interlocked.Exchange(ref _registration, 0);
        if (registration == 0)
        {
            return;
        }

        lock (Gate)
        {
            Callbacks.Remove(_id);
        }

        var result = PowerUnregisterFromEffectivePowerModeNotifications(registration);
        if (result < 0)
        {
            // The callback is already gone from the table, so a registration Windows keeps calls nothing.
            Log.Warn($"PowerUnregisterFromEffectivePowerModeNotifications failed (HRESULT 0x{result:X8}).");
        }
    }

    /// <summary>Registers <paramref name="changed" /> for effective power mode changes.</summary>
    /// <param name="changed">Called on a thread-pool thread; an exception it throws is logged and contained.</param>
    /// <returns>The registration, which stops the notifications when disposed.</returns>
    /// <exception cref="Exception">The HRESULT Windows returned, when it refused the registration.</exception>
    internal static EffectivePowerModeNotification Register(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        nint id;
        lock (Gate)
        {
            id = ++_lastId;
            Callbacks.Add(id, changed);
        }

        var result = PowerRegisterForEffectivePowerModeNotifications(
            EffectivePowerModeV2, &OnChanged, id, out var registration);
        if (result >= 0)
        {
            return new EffectivePowerModeNotification(id, registration);
        }

        lock (Gate)
        {
            Callbacks.Remove(id);
        }

        throw Marshal.GetExceptionForHR(result)!;
    }

    [UnmanagedCallersOnly]
    private static void OnChanged(int mode, nint context)
    {
        // An exception must never cross this native boundary: escaping an UnmanagedCallersOnly method
        // terminates the process.
        try
        {
            Action? changed;
            lock (Gate)
            {
                Callbacks.TryGetValue(context, out changed);
            }

            changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"Windows power mode notification handler failed: {ex.Message}");
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial int PowerRegisterForEffectivePowerModeNotifications(
        uint version, delegate* unmanaged<int, nint, void> callback, nint context, out nint registration);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerUnregisterFromEffectivePowerModeNotifications(nint registration);
}
