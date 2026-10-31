using System;
using Avalonia.Threading;

namespace WSGM.Shell;

/// <summary>The UI-thread post that background owners are handed instead of the dispatcher.</summary>
internal static class UiThread
{
    /// <summary>Queues <paramref name="action" /> on the Avalonia UI thread.</summary>
    /// <param name="action">The work to run there.</param>
    internal static void Post(Action action)
    {
        Dispatcher.UIThread.Post(action);
    }
}
