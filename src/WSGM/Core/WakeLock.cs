using System;
using System.ComponentModel;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>WSGM logging adapter for a reusable Windows power-request owner.</summary>
public sealed class WakeLock : IDisposable
{
    private readonly WindowsPowerRequest _request;

    /// <summary>Creates an inert request. Windows is touched only on Acquire.</summary>
    /// <param name="reason">Diagnostic reason reported by Windows.</param>
    /// <param name="requestType">0 holds the display; 1 holds automatic sleep.</param>
    public WakeLock(string reason, int requestType = 1) =>
        _request = new WindowsPowerRequest(reason, (WindowsPowerRequestKind)requestType);

    /// <summary>Whether the request was successfully set and has not been released.</summary>
    public bool IsHeld => _request.IsHeld;

    /// <summary>Acquires once, logging a refusal without interrupting the owning feature.</summary>
    public bool Acquire()
    {
        try { _request.Acquire(); return true; }
        catch (Win32Exception ex) { Log.Warn($"Keep awake: acquire failed (error {ex.NativeErrorCode})."); return false; }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>Releases once. A failed clear remains held until explicit retry or disposal.</summary>
    public void Release()
    {
        try { _request.Release(); }
        catch (Win32Exception ex) { Log.Warn($"Keep awake: release failed (error {ex.NativeErrorCode})."); }
    }

    /// <summary>Disposes the Windows handle and diagnostic buffer.</summary>
    public void Dispose() => _request.Dispose();
}
