using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Paces one artwork provider's requests and remembers its answers for the session.</summary>
/// <remarks>
///     <para>
///         Every provider needs the same three things, and each once had its own copy that had already
///         drifted: a bound on requests in flight, so bulk work cannot run the user into the provider's
///         rate limit; a bounded memory of answers, so the same page is not fetched once per artwork
///         type; and a check of that memory both before and behind the bound, so a duplicate queued
///         behind the first request finds its answer instead of repeating it.
///     </para>
///     <para>
///         A request made inside <see cref="Background" /> waits behind every interactive one. The
///         Game Library gathers artwork for a whole scan in the background, and without this the
///         artwork page the user just opened, or the match they are fixing, queued behind it.
///     </para>
///     <para>
///         A failure is remembered briefly too. Five lookups of one game go out together; when the
///         first fails, the four behind it get the same failure at once instead of each spending the
///         provider's full timeout to learn it again.
///     </para>
/// </remarks>
internal sealed class ArtworkRequestGate
{
    private static readonly AsyncLocal<bool> BackgroundScope = new();
    private static readonly TimeSpan FailureMemory = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, JsonElement> _answers = new(StringComparer.Ordinal);
    private readonly LinkedList<TaskCompletionSource> _background = new();
    private readonly int _capacity;

    private readonly Dictionary<string, (ArtworkProviderException Failure, DateTime Until)> _failures =
        new(StringComparer.Ordinal);

    private readonly LinkedList<TaskCompletionSource> _interactive = new();
    private readonly Queue<string> _order = new();
    private readonly int _remembered;
    private readonly Lock _sync = new();
    private ArtworkProviderException? _pause;
    private DateTimeOffset? _pausedUntil;
    private int _running;

    /// <summary>Creates a gate.</summary>
    /// <param name="capacity">How many requests may be in flight at once.</param>
    /// <param name="remembered">How many answers are kept, the oldest dropped first.</param>
    internal ArtworkRequestGate(int capacity, int remembered)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(remembered);
        _capacity = capacity;
        _remembered = remembered;
    }

    /// <summary>Why this provider is paused, until its allowance resets or credentials change.</summary>
    internal string? PauseReason
    {
        get
        {
            lock (_sync)
            {
                ExpirePause();
                return _pause?.Message;
            }
        }
    }

    /// <summary>Marks every request made until the scope is disposed as background work.</summary>
    /// <returns>The scope; dispose it to end it.</returns>
    /// <remarks>Flows through awaits, so a caller marks its own work without threading a flag through.</remarks>
    internal static IDisposable Background()
    {
        var previous = BackgroundScope.Value;
        BackgroundScope.Value = true;
        return new Scope(previous);
    }

    /// <summary>Answers from memory, or runs the request through the gate and remembers its answer.</summary>
    /// <param name="key">What identifies the request, such as its URL without credentials.</param>
    /// <param name="fetch">Makes the request.</param>
    /// <param name="cancellationToken">Cancels the wait and the request.</param>
    /// <returns>The answer, or null when the provider had none.</returns>
    /// <exception cref="ArtworkProviderException">The request failed, now or within the last half minute.</exception>
    internal async Task<JsonElement?> CachedAsync(
        string key, Func<CancellationToken, Task<JsonElement?>> fetch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        if (TryRecall(key, out var remembered))
        {
            return remembered;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryRecall(key, out remembered))
            {
                return remembered;
            }

            lock (_sync)
            {
                ThrowIfPaused();
            }

            JsonElement? answer;
            try
            {
                answer = await fetch(cancellationToken).ConfigureAwait(false);
            }
            catch (ArtworkProviderException failure)
            {
                lock (_sync)
                {
                    var now = DateTime.UtcNow;
                    foreach (var expired in _failures.Where(pair => pair.Value.Until <= now)
                                 .Select(pair => pair.Key).ToArray())
                    {
                        _failures.Remove(expired);
                    }

                    _failures[key] = (failure, now + FailureMemory);
                }

                throw;
            }

            if (answer is { } value)
            {
                Remember(key, value);
            }

            return answer;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Runs one request through the gate without remembering it: a download.</summary>
    /// <typeparam name="T">What the request answers.</typeparam>
    /// <param name="request">Makes the request.</param>
    /// <param name="cancellationToken">Cancels the wait and the request.</param>
    /// <returns>The request's answer.</returns>
    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ThrowIfPaused();
            }

            return await request(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Forgets every answer and failure, for when the credentials changed.</summary>
    internal void Clear()
    {
        lock (_sync)
        {
            _answers.Clear();
            _order.Clear();
            _failures.Clear();
            _pause = null;
            _pausedUntil = null;
        }
    }

    /// <summary>Stops queued network work after the provider refused the account or its allowance.</summary>
    internal ArtworkProviderException Pause(string message, string providerId, DateTimeOffset? until = null)
    {
        lock (_sync)
        {
            _pause = new ArtworkProviderException(message, providerId, true);
            _pausedUntil = until;
            return _pause;
        }
    }

    private void ExpirePause()
    {
        if (_pausedUntil is { } until && until <= DateTimeOffset.UtcNow)
        {
            _pause = null;
            _pausedUntil = null;
            _failures.Clear();
        }
    }

    private void ThrowIfPaused()
    {
        ExpirePause();
        if (_pause is { } pause)
        {
            throw new ArtworkProviderException(pause.Message, pause.ProviderId, true);
        }
    }

    private bool TryRecall(string key, out JsonElement? answer)
    {
        lock (_sync)
        {
            if (_answers.TryGetValue(key, out var value))
            {
                answer = value;
                return true;
            }

            if (_failures.TryGetValue(key, out var failure))
            {
                if (failure.Until > DateTime.UtcNow)
                {
                    throw new ArtworkProviderException(failure.Failure.Message, failure.Failure.ProviderId,
                        failure.Failure.Paused);
                }

                _failures.Remove(key);
            }
        }

        answer = null;
        return false;
    }

    private void Remember(string key, JsonElement value)
    {
        lock (_sync)
        {
            if (_remembered == 0 || !_answers.TryAdd(key, value))
            {
                return;
            }

            _order.Enqueue(key);
            while (_order.Count > _remembered && _order.TryDequeue(out var oldest))
            {
                _answers.Remove(oldest);
            }
        }
    }

    private Task EnterAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (_sync)
        {
            // Nobody queues while a slot is free: Exit hands a slot straight to a waiter rather than
            // freeing it, so a free slot means both queues are empty.
            if (_running < _capacity)
            {
                _running++;
                return Task.CompletedTask;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = (BackgroundScope.Value ? _background : _interactive).AddLast(waiter);
        }

        if (!cancellationToken.CanBeCanceled)
        {
            return waiter.Task;
        }

        var registration = cancellationToken.Register(() =>
        {
            lock (_sync)
            {
                // Still queued: take it out. Already admitted: the admission stands and the caller's
                // finally hands the slot back.
                if (node.List is not null)
                {
                    node.List.Remove(node);
                    waiter.TrySetCanceled(cancellationToken);
                }
            }
        });
        return waiter.Task.ContinueWith(
            completed =>
            {
                registration.Dispose();
                return completed;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }

    private void Exit()
    {
        lock (_sync)
        {
            // The slot passes straight to the next waiter, interactive first, so it is never counted
            // free while someone is queued for it.
            var next = _interactive.First ?? _background.First;
            if (next is null)
            {
                _running--;
                return;
            }

            next.List!.Remove(next);
            next.Value.TrySetResult();
        }
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose()
        {
            BackgroundScope.Value = previous;
        }
    }
}

/// <summary>An artwork provider could not answer: a failure the user should see, never "no images".</summary>
/// <param name="message">What went wrong, in words the page shows.</param>
/// <param name="providerId">The provider that failed, when known.</param>
/// <param name="paused">Whether further requests must wait for credentials or an allowance reset.</param>
internal class ArtworkProviderException(string message, string providerId = "", bool paused = false)
    : Exception(message)
{
    internal string ProviderId { get; } = providerId;
    internal bool Paused { get; } = paused;
}
