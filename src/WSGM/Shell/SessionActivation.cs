using System;
using System.Threading;
using Avalonia.Threading;

namespace WSGM.Shell;

/// <summary>Hands shortcut activation to the mutex-owning session without starting another runtime.</summary>
internal sealed class SessionActivation : IDisposable
{
    internal const string EventName = @"Local\WSGM.Activate";
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _wait;
    private bool _disposed;

    internal SessionActivation(Action activate, string eventName = EventName)
    {
        _signal = new(false, EventResetMode.AutoReset, eventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal,
            (_, _) => Dispatcher.UIThread.Post(() => { if (!_disposed) { activate(); } }),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _disposed = true;
        _wait.Unregister(null);
        _signal.Dispose();
    }
}
