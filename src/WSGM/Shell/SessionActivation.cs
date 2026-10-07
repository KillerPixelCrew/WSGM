using System;
using System.Threading;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Hands shortcut activation to the mutex-owning session without starting another runtime.</summary>
internal sealed class SessionActivation : IDisposable
{
    /// <summary>Per-session named event used by secondary WSGM activations.</summary>
    internal const string EventName = @"Local\WSGM.Activate";
    private readonly EventWaitHandle? _signal;
    private readonly RegisteredWaitHandle? _wait;
    private bool _disposed;

    /// <summary>Registers activation delivery to the Avalonia dispatcher when the named event can be opened.</summary>
    /// <param name="activate">UI-thread activation callback, ignored after disposal.</param>
    /// <param name="eventName">Named auto-reset event shared with secondary processes.</param>
    internal SessionActivation(Action activate, string eventName = EventName)
    {
        _signal = TryCreateSignal(eventName);
        if (_signal is null)
        {
            return;
        }

        _wait = ThreadPool.RegisterWaitForSingleObject(_signal,
            (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!Volatile.Read(ref _disposed))
                {
                    activate();
                }
            }),
            null, Timeout.Infinite, false);
    }

    /// <summary>Unregisters the wait and releases the event; queued dispatcher callbacks become no-ops.</summary>
    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        _wait?.Unregister(null);
        _signal?.Dispose();
    }

    /// <summary>Creates or opens the activation event with the shared event-access policy.</summary>
    /// <param name="eventName">Session-local event identity.</param>
    /// <returns>A caller-owned event handle, or null when the native event cannot be opened.</returns>
    internal static EventWaitHandle? TryCreateSignal(string eventName = EventName)
    {
        var handle = UpdateExitWatcher.CreateOrOpenEvent(eventName, "Shell activation", null,
            false, false);
        if (handle == 0)
        {
            return null;
        }

        var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        var unnamed = signal.SafeWaitHandle;
        signal.SafeWaitHandle = new SafeWaitHandle(handle, true);
        unnamed.Dispose();
        return signal;
    }
}
