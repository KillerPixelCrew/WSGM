using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The library-tab boot sync: one worker, started by the first request, that waits for Steam's UI,
///     then injects the tabs and pushes the badge map. A request supersedes the pass in flight; a cancel
///     drops it and any request not yet started.
/// </summary>
/// <param name="libraryManager">
///     Reads the session's library manager, which runs the sync, or null in overlay-test, where a pass does
///     nothing.
/// </param>
/// <param name="readiness">The session's Steam UI readiness the pass waits on.</param>
/// <param name="stopping">Whether the session is stopping, after which no request is accepted.</param>
/// <param name="lifetime">The session's lifetime; cancelling it ends the worker.</param>
internal sealed class LibraryTabBootSync(
    Func<LibraryManagerService?> libraryManager,
    SteamUiReadiness readiness,
    Func<bool> stopping,
    CancellationToken lifetime)
{
    // Only the worker creates and disposes a pass's source, always under _gate, so a disposed source
    // is never cancelled.
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private CancellationTokenSource? _pass;
    private bool _requested;
    private Task? _worker;

    /// <summary>Gets the worker, or a completed task when no request started it.</summary>
    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _worker ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>
    ///     Asks for a fresh pass, superseding the one in flight. Safe to call on every trigger: rapid
    ///     requests collapse into one pass after the last of them.
    /// </summary>
    internal void Kick()
    {
        lock (_gate)
        {
            if (stopping())
            {
                return;
            }

            _requested = true;
            _pass?.Cancel();
            _worker ??= Task.Run(RunAsync);
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
    }

    /// <summary>
    ///     Asks for a pass at every new ready SharedJsContext generation of <paramref name="transport" />: the
    ///     first time Steam's UI context exists and again after Steam replaces it, which drops the injected
    ///     tabs with the old document.
    /// </summary>
    /// <param name="transport">The session's Steam UI transport.</param>
    /// <param name="admitted">
    ///     Whether automatic Steam UI work may run now; read on the transport's thread, so it must be safe there.
    /// </param>
    /// <returns>The subscription; disposing it stops following.</returns>
    /// <remarks>
    ///     The readiness gate alone is not enough. On the desktop it opens on the master switch, before Steam
    ///     has a UI context, so the first pass found no target and waited for a ready edge that never came
    ///     while the gate stayed open: the tabs were never installed (wsgm.log, 2026-11-07 00:09:26).
    /// </remarks>
    internal IDisposable Follow(ISteamUiTransport transport, Func<bool> admitted)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(admitted);
        transport.GenerationChanged += OnGenerationChanged;
        return new Subscription(() => transport.GenerationChanged -= OnGenerationChanged);

        void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
        {
            if (snapshot is { Role: SteamUiTargetRole.SharedJsContext, Health: SteamUiTransportHealth.Ready }
                && admitted())
            {
                Kick();
            }
        }
    }

    /// <summary>
    ///     Stops the pass in flight and drops a request not yet started, so nothing re-injects the tabs
    ///     during a retraction. The next <see cref="Kick" /> runs normally.
    /// </summary>
    internal void Cancel()
    {
        lock (_gate)
        {
            _requested = false;
            _pass?.Cancel();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(lifetime).ConfigureAwait(false);
                CancellationTokenSource pass;
                lock (_gate)
                {
                    if (!_requested)
                    {
                        continue;
                    }

                    _requested = false;
                    pass = _pass = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                }

                try
                {
                    if (libraryManager() is { } manager)
                    {
                        await manager.SyncTabsOnBootAsync(readiness, pass.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (pass.IsCancellationRequested)
                {
                    Log.Info("Library tab boot sync superseded or cancelled.");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Without this a failed boot sync left the tabs missing with nothing in the log.
                    Log.Warn($"Library tab boot sync failed: {ex.Message}");
                }
                finally
                {
                    lock (_gate)
                    {
                        _pass = null;
                        pass.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Session shutdown.
        }
    }

    /// <summary>Runs its release once.</summary>
    private sealed class Subscription(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
