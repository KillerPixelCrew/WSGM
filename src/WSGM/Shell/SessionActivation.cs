using System;
using System.Threading;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Hands shortcut activation to the mutex-owning session without starting another runtime.</summary>
internal sealed class SessionActivation : IDisposable
{
    internal const string EventName = @"Local\WSGM.Activate";
    private readonly EventWaitHandle? _signal;
    private readonly RegisteredWaitHandle? _wait;
    private bool _disposed;

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

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        _wait?.Unregister(null);
        _signal?.Dispose();
    }

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
