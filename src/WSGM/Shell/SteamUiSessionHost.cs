using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// Owns the process-long persistent Steam UI transport, narrow bridge, and registered patches.
/// </summary>
internal sealed class SteamUiSessionHost : IAsyncDisposable
{
    private readonly PersistentSteamUiTransport _transport = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _synchronizeSignal = new(0, 1);
    private readonly Func<CancellationToken, Task<bool>> _toggleQuickAccess;
    private readonly SteamUiBridgeHost _bridge;
    private readonly SteamUiPatchManager _patches;
    private readonly Task _synchronization;
    private int _signalPending;
    private volatile bool _enabled;
    private volatile bool _disposed;

    internal SteamUiSessionHost(Func<CancellationToken, Task<bool>> toggleQuickAccess)
    {
        ArgumentNullException.ThrowIfNull(toggleQuickAccess);
        _toggleQuickAccess = toggleQuickAccess;
        _bridge = new SteamUiBridgeHost(_transport);
        _patches = new SteamUiPatchManager(_transport);
        _patches.Register(new NativeQamBootstrapPatch(_bridge));
        _bridge.RequestReceived += OnRequestReceived;
        _transport.GenerationChanged += OnGenerationChanged;
        _synchronization = Task.Run(SynchronizeLoopAsync);
    }

    internal void Apply(bool enabled)
    {
        if (_disposed || _enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        _patches.SetGlobalEnabled(enabled);
        _patches.SetPatchEnabled("wsgm.native-qam.bootstrap", enabled);
        QueueSynchronization();
    }

    internal async Task DisableAsync()
    {
        if (_disposed)
        {
            return;
        }

        _enabled = false;
        _patches.SetGlobalEnabled(false);
        _patches.SetPatchEnabled("wsgm.native-qam.bootstrap", false);
        await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        if (_enabled && snapshot.Role == SteamUiTargetRole.SharedJsContext)
        {
            QueueSynchronization();
        }
    }

    private void QueueSynchronization()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _signalPending, 1) == 0)
        {
            _synchronizeSignal.Release();
        }
    }

    private async Task SynchronizeLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _synchronizeSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _signalPending, 0);
                await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam UI patch synchronization failed: {ex.Message}");
            }
        }
    }

    private void OnRequestReceived(object? sender, SteamUiBridgeRequest request)
    {
        _ = RespondToRequestAsync(request);
    }

    private async Task RespondToRequestAsync(SteamUiBridgeRequest request)
    {
        bool succeeded = false;
        string? error = null;
        try
        {
            if (request.Type == "cancel")
            {
                succeeded = true;
            }
            else if (request.PatchId == "wsgm.native-qam.shell"
                && request.Command == "toggleQuickAccess")
            {
                succeeded = await _toggleQuickAccess(_shutdown.Token).ConfigureAwait(false);
                if (!succeeded)
                {
                    error = "Quick access is not currently available.";
                }
            }
            else
            {
                error = "The requested semantic service is not active.";
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        try
        {
            await _bridge.RespondAsync(request, succeeded, null, error, _shutdown.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam UI bridge response failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.GenerationChanged -= OnGenerationChanged;
        _bridge.RequestReceived -= OnRequestReceived;
        _enabled = false;
        _patches.SetGlobalEnabled(false);
        _shutdown.Cancel();
        try
        {
            await _synchronization.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _patches.DisposeAsync().ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _synchronizeSignal.Dispose();
        _shutdown.Dispose();
    }
}
