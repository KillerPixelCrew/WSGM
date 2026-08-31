using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Runs the two traffic directions between registered modules and the injected side: state pushed
/// out, and commands answered back.
/// </summary>
/// <remarks>
/// This is the half of hosting that is the same for any consumer. What it deliberately does NOT
/// own is which patches should be applied when — that is the host's policy, it differs per
/// application, and pulling it in here would mean a constructor full of predicates that only
/// describe one host's rules.
/// <para>
/// One rule here is load-bearing rather than incidental: <b>every refusal is logged with its
/// reason</b>. The reason is built by the module and handed straight back to the injected side,
/// which has nowhere to put it, so a control the user operated that quietly did nothing would
/// otherwise leave no trace at all on this side of the bridge. That defect cost a session — Steam
/// had a 28 W limit stored, the gate had forwarded it, and the hardware was still at 30 W with not
/// one line saying why.
/// </para>
/// </remarks>
public sealed class SteamUiModuleRuntime : IAsyncDisposable
{
    private readonly SteamUiBridgeHost _bridge;
    private readonly SteamUiModuleSet _modules;
    private readonly Func<bool> _commandsEnabled;
    private readonly Func<bool> _publishEnabled;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _publicationSignal = new(0, 1);
    private readonly object _requestGate = new();
    private readonly Dictionary<long, CancellationTokenSource> _inflight = [];
    private readonly HashSet<Task> _requestTasks = [];
    private readonly Task _publication;
    private int _publicationPending;
    private bool _disposed;

    /// <summary>Starts the publication pump and begins answering bridge requests.</summary>
    /// <param name="bridge">The bridge this runtime publishes through and answers on.</param>
    /// <param name="modules">The registered modules supplying publications and command handlers.</param>
    /// <param name="commandsEnabled">Whether commands may be answered at all right now. A command
    /// arriving while this is false is refused with a reason rather than dropped.</param>
    /// <param name="publishEnabled">Whether any state may be published this round. Evaluated once
    /// per round; each publication's own gate is evaluated separately.</param>
    public SteamUiModuleRuntime(
        SteamUiBridgeHost bridge,
        SteamUiModuleSet modules,
        Func<bool> commandsEnabled,
        Func<bool> publishEnabled)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _commandsEnabled = commandsEnabled ?? throw new ArgumentNullException(nameof(commandsEnabled));
        _publishEnabled = publishEnabled ?? throw new ArgumentNullException(nameof(publishEnabled));
        _bridge.RequestReceived += OnRequestReceived;
        _publication = Task.Run(PublishLoopAsync);
    }

    /// <summary>Asks for one publication round, coalescing repeats into the pending one.</summary>
    public void QueuePublication()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _publicationPending, 1) == 0)
        {
            _publicationSignal.Release();
        }
    }

    /// <summary>Cancels every request still in flight.</summary>
    /// <remarks>
    /// Called when a generation is replaced. A semantic operation is authorized against one
    /// execution-context and document pair, so letting it continue after either moved could apply
    /// a result for a page that can no longer receive its response — replacement is cancellation,
    /// exactly like an explicit cancel from the injected side.
    /// </remarks>
    public void CancelAllInflight()
    {
        CancellationTokenSource[] inflight;
        lock (_requestGate)
        {
            inflight = [.. _inflight.Values];
        }

        foreach (CancellationTokenSource cancellation in inflight)
        {
            CancelSafely(cancellation);
        }
    }

    private void OnRequestReceived(object? sender, SteamUiBridgeRequest request)
    {
        if (request.Type == "cancel")
        {
            CancelInflight(request.Sequence);
            return;
        }

        Task task = RespondAsync(request);
        lock (_requestGate)
        {
            _requestTasks.Add(task);
        }
        _ = ObserveCompletionAsync(task);
    }

    private async Task RespondAsync(SteamUiBridgeRequest request)
    {
        using CancellationTokenSource requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_requestGate)
        {
            if (!_inflight.TryAdd(request.Sequence, requestCancellation))
            {
                return;
            }
        }

        SteamUiCommandResult outcome;
        try
        {
            if (!_commandsEnabled())
            {
                outcome = SteamUiCommandResult.Refused;
            }
            else if (!_modules.TryGetCommand(
                request.PatchId,
                request.Command,
                out SteamUiCommandDelegate? handler)
                || handler is null)
            {
                outcome = SteamUiCommandResult.Refused;
            }
            else
            {
                outcome = await handler(request, requestCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            RemoveInflight(request.Sequence);
            return;
        }
        catch (Exception ex)
        {
            outcome = new SteamUiCommandResult(false, ex.Message);
        }

        // Keyed per patch and command, because a gate can repeat a refused write on its own
        // schedule: the first prints, the repeats are counted.
        if (!outcome.Succeeded)
        {
            SteamUiLog.Change(
                $"steam.ui.request.{request.PatchId}.{request.Command}",
                $"Steam UI request {request.PatchId}/{request.Command} did nothing: "
                    + (outcome.Error ?? "no reason reported"),
                warning: true);
        }

        try
        {
            if (requestCancellation.IsCancellationRequested)
            {
                return;
            }

            await _bridge.RespondAsync(
                    request,
                    outcome.Succeeded,
                    outcome.Payload,
                    outcome.Error,
                    requestCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Steam UI bridge response failed: {ex.Message}");
        }
        finally
        {
            RemoveInflight(request.Sequence);
        }
    }

    private async Task PublishLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _publicationSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _publicationPending, 0);
                if (!_publishEnabled() || !_bridge.IsReady)
                {
                    continue;
                }

                foreach (SteamUiStatePublication publication in _modules.Publications)
                {
                    if (!publication.Enabled())
                    {
                        continue;
                    }
                    JsonElement? payload = await publication.Read().ConfigureAwait(false);
                    // Null publishes nothing this round, which keeps a reading that is momentarily
                    // unavailable distinct from a zero.
                    if (payload is { } state)
                    {
                        await _bridge.PublishStateAsync(
                                publication.PatchId,
                                state,
                                _shutdown.Token)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SteamUiLog.Warn($"Steam UI semantic state publication failed: {ex.Message}");
            }
        }
    }

    private void CancelInflight(long sequence)
    {
        CancellationTokenSource? cancellation;
        lock (_requestGate)
        {
            _inflight.TryGetValue(sequence, out cancellation);
        }

        CancelSafely(cancellation);
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request completed between the bounded lookup and cancellation.
        }
    }

    private void RemoveInflight(long sequence)
    {
        lock (_requestGate)
        {
            _inflight.Remove(sequence);
        }
    }

    private async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Steam UI semantic request failed unexpectedly: {ex.Message}");
        }
        finally
        {
            lock (_requestGate)
            {
                _requestTasks.Remove(task);
            }
        }
    }

    /// <summary>Stops answering, drains in-flight work, and releases the pump.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _bridge.RequestReceived -= OnRequestReceived;
        CancelAllInflight();
        _shutdown.Cancel();
        try
        {
            await _publication.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Task[] requestTasks;
        lock (_requestGate)
        {
            requestTasks = [.. _requestTasks];
        }
        try
        {
            await Task.WhenAll(requestTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Steam UI semantic request cleanup failed: {ex.Message}");
        }

        _publicationSignal.Dispose();
        _shutdown.Dispose();
    }
}
