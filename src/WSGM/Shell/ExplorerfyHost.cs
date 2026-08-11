using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Listens for the <c>WSGM.Explorerfy</c> launch wrapper (games that need
/// Windows Explorer running) and, while a wrapped game holds a connection, drops
/// the shell to desktop mode via <see cref="SessionModes.EnterDesktopMode"/> — which
/// brings Explorer up correctly (tray host torn down, de-elevated, DPI restored) —
/// then returns to game mode when the last wrapper disconnects.
/// <para>The pipe connection is the lease: a wrapper killed by Steam breaks its
/// pipe, which releases the lease exactly as a clean exit would. Requests are
/// serialized through <see cref="SessionModes"/>'s own transition guard, so this
/// never fights the overlay's mode buttons.</para></summary>
public sealed class ExplorerfyHost : IDisposable
{
    private const string PipeName = "WSGM.Explorerfy";
    private const byte AcquireRequest = 1;
    private const byte ReadyAck = 1;
    private const byte NotReadyAck = 0;

    private readonly SessionModes _modes;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private int _active;
    private bool _weEnteredDesktop;

    private ExplorerfyHost(SessionModes modes) => _modes = modes;

    /// <summary>Starts the listener. The accept loop runs for the shell's lifetime.</summary>
    /// <param name="modes">The session-mode coordinator to drive desktop/game transitions.</param>
    public static ExplorerfyHost StartNew(SessionModes modes)
    {
        var host = new ExplorerfyHost(modes);
        _ = host.AcceptLoopAsync();
        Log.Info("Explorerfy host listening for Explorer-needing games.");
        return host;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex)
            {
                Log.Warn($"Explorerfy host could not create its pipe: {ex.Message}");
                if (await DelayOrStopAsync())
                {
                    return;
                }
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Explorerfy host accept failed: {ex.Message}");
                server.Dispose();
                if (await DelayOrStopAsync())
                {
                    return;
                }
                continue;
            }

            // One handler per client; the next accept starts immediately so
            // concurrent wrapped games are each served.
            _ = HandleClientAsync(server);
        }
    }

    private async Task<bool> DelayOrStopAsync()
    {
        try
        {
            await Task.Delay(1000, _cts.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server)
    {
        var acquired = false;
        try
        {
            var request = new byte[1];
            await server.ReadExactlyAsync(request, _cts.Token);
            if (request[0] != AcquireRequest)
            {
                return;
            }

            acquired = true;
            var ready = await OnAcquireAsync();
            await server.WriteAsync(new[] { ready ? ReadyAck : NotReadyAck }, _cts.Token);
            await server.FlushAsync(_cts.Token);

            // Hold the lease until the wrapper disconnects — clean game exit or
            // Steam killing the wrapper both surface here (0 bytes / broken pipe).
            var drain = new byte[1];
            try
            {
                while (await server.ReadAsync(drain, _cts.Token) != 0)
                {
                    // The wrapper sends nothing after acquire; ignore stray bytes.
                }
            }
            catch (IOException)
            {
                // Broken pipe is the expected release signal.
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
        catch (Exception ex)
        {
            Log.Warn($"Explorerfy client handling failed: {ex.Message}");
        }
        finally
        {
            if (acquired)
            {
                OnRelease();
            }
            try { server.Dispose(); } catch { }
        }
    }

    private async Task<bool> OnAcquireAsync()
    {
        bool first;
        lock (_gate)
        {
            _active++;
            first = _active == 1;
        }
        if (!first)
        {
            // Another wrapped game already holds Explorer up.
            return true;
        }

        // Already on the desktop (the user switched there): nothing to bring up, and
        // we must NOT force game mode on release — leave the user where they are.
        if (ExplorerControl.IsRunningInSession())
        {
            lock (_gate) { _weEnteredDesktop = false; }
            Log.Info("Explorerfy: a wrapped game needs Explorer; it is already running (desktop mode).");
            return true;
        }

        lock (_gate) { _weEnteredDesktop = true; }
        Log.Info("Explorerfy: a wrapped game needs Explorer — entering desktop mode.");
        await RunOnUiAsync(() => _modes.EnterDesktopMode());

        // Wait (off the UI thread) for Explorer to actually come up before acking,
        // so the game/mod tool finds a live shell. Bounded; we launch either way.
        for (var i = 0; i < 25 && !ExplorerControl.IsRunningInSession(); i++)
        {
            try { await Task.Delay(200, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
        return ExplorerControl.IsRunningInSession();
    }

    private void OnRelease()
    {
        bool last;
        bool weEntered;
        lock (_gate)
        {
            _active = Math.Max(0, _active - 1);
            last = _active == 0;
            weEntered = _weEnteredDesktop;
            if (last)
            {
                _weEnteredDesktop = false;
            }
        }
        if (!last || !weEntered)
        {
            return;
        }
        Log.Info("Explorerfy: wrapped game exited — returning to game mode.");
        _ = RunOnUiAsync(() => _modes.EnterGameMode());
    }

    private static Task RunOnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex)
            {
                Log.Warn($"Explorerfy UI action failed: {ex.Message}");
                tcs.SetResult();
            }
        });
        return tcs.Task;
    }

    /// <summary>Stops the listener. In-flight leases are released by their pipes closing.</summary>
    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
