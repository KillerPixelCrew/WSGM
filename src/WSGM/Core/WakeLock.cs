using System;
using System.Runtime.InteropServices;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>One Windows power-request object (a "wake lock") that, while held, keeps the
/// system in its working state: the display still turns off on its own timeout, but the
/// standby entry that would normally follow is blocked. Windows documents the limits —
/// the block is indefinite on AC power, on battery the request is force-terminated five
/// minutes after the sleep timeout expires, and a user-initiated sleep (power button,
/// Start menu) always wins. The reason string is what <c>powercfg /requests</c> reports,
/// so keep it specific enough to diagnose from a device log alone. The request object
/// dies with the process, so a crash can never leave the device sleepless.</summary>
public sealed class WakeLock : IDisposable
{
    private readonly object _gate = new();
    private readonly string _reason;
    private readonly int _requestType;
    private nint _reasonBuffer;
    private nint _request;
    private bool _held;
    private bool _disposed;

    /// <summary>Creates the lock without touching Windows yet.</summary>
    /// <param name="reason">The diagnostic reason shown by <c>powercfg /requests</c>.</param>
    /// <param name="requestType">The POWER_REQUEST_TYPE to hold; defaults to
    /// SystemRequired (block standby, let the display turn off).</param>
    public WakeLock(string reason, int requestType = NativeMethods.PowerRequestSystemRequired)
    {
        _reason = reason;
        _requestType = requestType;
    }

    /// <summary>Whether this lock's configured power request — the
    /// <c>requestType</c> the constructor was given — is currently set.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    /// <summary>Sets this lock's configured power request (idempotent). Failures are
    /// logged and reported as false — a keep-awake that cannot engage must never take
    /// a feature down with it.</summary>
    public bool Acquire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
            if (_held)
            {
                return true;
            }
            if (_request == 0)
            {
                // The kernel object keeps referencing the reason string; the buffer
                // lives until Dispose (see ReasonContext).
                _reasonBuffer = Marshal.StringToHGlobalUni(_reason);
                var context = new NativeMethods.ReasonContext
                {
                    Version = NativeMethods.PowerRequestContextVersion,
                    Flags = NativeMethods.PowerRequestContextSimpleString,
                    SimpleReasonString = _reasonBuffer,
                };
                var handle = NativeMethods.PowerCreateRequest(in context);
                if (handle == 0 || handle == -1)
                {
                    Log.Warn($"Keep awake: PowerCreateRequest failed "
                        + $"(error {Marshal.GetLastPInvokeError()}).");
                    Marshal.FreeHGlobal(_reasonBuffer);
                    _reasonBuffer = 0;
                    return false;
                }
                _request = handle;
            }
            if (!NativeMethods.PowerSetRequest(_request, _requestType))
            {
                Log.Warn($"Keep awake: PowerSetRequest failed "
                    + $"(error {Marshal.GetLastPInvokeError()}).");
                return false;
            }
            _held = true;
            return true;
        }
    }

    /// <summary>Clears this lock's configured power request (idempotent). A failed
    /// clear leaves the lock held: Windows is still blocking standby, so
    /// <see cref="IsHeld"/> must keep saying so and <see cref="Dispose"/> retries.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (!_held)
            {
                return;
            }
            if (_request != 0
                && !NativeMethods.PowerClearRequest(_request, _requestType))
            {
                Log.Warn($"Keep awake: PowerClearRequest failed "
                    + $"(error {Marshal.GetLastPInvokeError()}).");
                return;
            }
            _held = false;
        }
    }

    /// <summary>Clears the request and closes the kernel object.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_held && _request != 0)
            {
                NativeMethods.PowerClearRequest(_request, _requestType);
                _held = false;
            }
            if (_request != 0)
            {
                NativeMethods.CloseHandle(_request);
                _request = 0;
            }
            if (_reasonBuffer != 0)
            {
                Marshal.FreeHGlobal(_reasonBuffer);
                _reasonBuffer = 0;
            }
        }
    }
}
