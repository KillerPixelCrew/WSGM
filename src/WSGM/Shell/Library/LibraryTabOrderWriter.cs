using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The one tab-order writer: waits until the move presses pause, saves the newest order once and
///     shows it in Steam, and runs again while newer presses arrived meanwhile. Closing skips the wait
///     and the push, so the last press is still saved.
/// </summary>
/// <remarks>
///     Its pending order and running flag are guarded by the manager's gate, the same lock that guards
///     the projection the pressed-to order is shown in, so a refresh never publishes an older stored
///     order over a press the writer has not saved yet.
/// </remarks>
/// <param name="gate">The library manager's gate.</param>
/// <param name="tabs">Saves the order.</param>
/// <param name="steam">Returns the session's Steam client, or null on a surface without one.</param>
/// <param name="syncQuietly">The full sync a push falls back to when Steam's resident script is missing.</param>
/// <param name="stop">The library manager's stop; it skips the wait and the push.</param>
internal sealed class LibraryTabOrderWriter(
    Lock gate,
    LibraryTabManager tabs,
    Func<SteamClient?> steam,
    Func<SteamClient, string, CancellationToken, Task> syncQuietly,
    CancellationToken stop)
{
    // How long the tab order waits after the last move press before it is saved and shown in Steam.
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(600);

    private LibraryTabOrderPress? _pending;

    /// <summary>Whether the writer runs; while it does, the stored order is older than the pressed one.</summary>
    /// <remarks>The caller holds the manager's gate.</remarks>
    internal bool Running { get; private set; }

    /// <summary>Replaces the pending order with a newer press.</summary>
    /// <param name="press">The order the user just pressed to.</param>
    /// <returns>Whether the caller must start <see cref="RunAsync" />; false while it already runs.</returns>
    /// <remarks>The caller holds the manager's gate.</remarks>
    internal bool Press(LibraryTabOrderPress press)
    {
        _pending = press;
        if (Running)
        {
            return false;
        }

        Running = true;
        return true;
    }

    /// <summary>Saves and shows pending orders until none is left.</summary>
    /// <returns>Completion once no press is pending.</returns>
    internal async Task RunAsync()
    {
        while (true)
        {
            LibraryTabOrderPress? waited;
            lock (gate)
            {
                waited = _pending;
            }

            try
            {
                await Task.Delay(WriteDelay, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            LibraryTabOrderPress press;
            lock (gate)
            {
                if (!ReferenceEquals(_pending, waited) && !stop.IsCancellationRequested)
                {
                    // A newer press restarts the wait.
                    continue;
                }

                press = _pending!;
                _pending = null;
            }

            var saved = false;
            try
            {
                await tabs.SaveTabOrderAsync(press.Keys, press.Hidden).ConfigureAwait(false);
                saved = true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Library tab order save failed: {ex.Message}");
            }

            if (saved && !stop.IsCancellationRequested && steam() is { } client)
            {
                await PushAsync(client, press).ConfigureAwait(false);
            }

            lock (gate)
            {
                if (_pending is null)
                {
                    Running = false;
                    break;
                }
            }
        }
    }

    /// <summary>Shows a saved tab-strip order in the running Steam.</summary>
    /// <remarks>
    ///     Cheap (no filter re-evaluation), so the strip follows while the user is still tapping move.
    ///     It falls back to a full sync when the resident script is not installed in this Steam
    ///     session yet. A failure is logged and waits for the next sync.
    /// </remarks>
    private async Task PushAsync(SteamClient client, LibraryTabOrderPress press)
    {
        try
        {
            if (!await SteamLibraryTabs.PushOrderAsync(client, press.Keys, press.Hidden, stop).ConfigureAwait(false))
            {
                await syncQuietly(client, "builder", stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Library tab order push failed: {ex.Message}");
        }
    }
}
