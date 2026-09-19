using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Threading;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Carries canonical controller samples from the plugin runtime's thread to the UI thread.</summary>
/// <remarks>
///     One dispatcher post drains everything queued since the previous drain, instead of a post and a
///     closure per sample. While a sample is still waiting, a newer one holding the same buttons and
///     triggers is dropped: the router acts only on edges, so it could not change anything. A lost
///     source is queued in order with the samples, so no sample is delivered ahead of it.
/// </remarks>
internal sealed class CanonicalSampleQueue
{
    private readonly Action _drain;
    private readonly Lock _gate = new();
    private readonly Action _sourceLost;
    private readonly Action<CanonicalControllerSample, GamepadButtons> _submit;
    private GamepadButtons _lastQueuedHeld;

    // Null entries are lost-source signals. Two lists swap between queueing and draining, so the
    // steady state allocates nothing.
    private List<QueuedSample?> _pending = [];
    private List<QueuedSample?>? _spare = [];

    /// <summary>Creates a queue that delivers on the UI thread.</summary>
    /// <param name="submit">Receives each delivered sample.</param>
    /// <param name="sourceLost">Receives each lost-source signal.</param>
    internal CanonicalSampleQueue(Action<CanonicalControllerSample, GamepadButtons> submit, Action sourceLost)
    {
        ArgumentNullException.ThrowIfNull(submit);
        ArgumentNullException.ThrowIfNull(sourceLost);
        _submit = submit;
        _sourceLost = sourceLost;
        _drain = Drain;
    }

    /// <summary>Queues a sample from any thread.</summary>
    /// <param name="sample">The sample.</param>
    internal void Enqueue(CanonicalControllerSample sample)
    {
        var held = UiInputRouter.Translate(sample);
        bool post;
        lock (_gate)
        {
            if (_pending is [.., not null] && held == _lastQueuedHeld)
            {
                return;
            }

            _lastQueuedHeld = held;
            post = _pending.Count == 0;
            _pending.Add(new QueuedSample(sample, held));
        }

        if (post)
        {
            Dispatcher.UIThread.Post(_drain);
        }
    }

    /// <summary>Queues a lost-source signal from any thread.</summary>
    internal void SourceLost()
    {
        bool post;
        lock (_gate)
        {
            post = _pending.Count == 0;
            _pending.Add(null);
        }

        if (post)
        {
            Dispatcher.UIThread.Post(_drain);
        }
    }

    private void Drain()
    {
        List<QueuedSample?> batch;
        lock (_gate)
        {
            batch = _pending;
            _pending = _spare ?? [];
            _spare = null;
        }

        try
        {
            foreach (var queued in batch)
            {
                if (queued is null)
                {
                    _sourceLost();
                }
                else
                {
                    _submit(queued.Value.Sample, queued.Value.Held);
                }
            }
        }
        finally
        {
            batch.Clear();
            lock (_gate)
            {
                _spare ??= batch;
            }
        }
    }

    private readonly record struct QueuedSample(CanonicalControllerSample Sample, GamepadButtons Held);
}
