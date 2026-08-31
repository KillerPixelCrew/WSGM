using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Owns one persistent bounded CDP connection for each allowlisted Steam UI target.</summary>
public sealed class PersistentSteamUiTransport : ISteamUiTransport
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    ];

    private readonly ISteamUiEndpointDiscovery _discovery;
    private readonly ISteamUiCdpWireFactory _wireFactory;
    private readonly Dictionary<SteamUiTargetRole, TargetChannel> _channels;
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _enabled = true;
    private int _disposed;

    /// <summary>Creates a production transport using Steam's validated loopback endpoint.</summary>
    public PersistentSteamUiTransport()
        : this(new SteamUiEndpointDiscovery(), new SteamUiWebSocketWireFactory())
    {
    }

    internal PersistentSteamUiTransport(
        ISteamUiEndpointDiscovery discovery, ISteamUiCdpWireFactory wireFactory)
    {
        _discovery = discovery;
        _wireFactory = wireFactory;
        _channels = Enum.GetValues<SteamUiTargetRole>().ToDictionary(
            role => role,
            role => new TargetChannel(role));
    }

    /// <inheritdoc />
    public event EventHandler<SteamUiNotification>? NotificationReceived;

    /// <inheritdoc />
    public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable> SubscribeAsync(
        SteamUiTargetRole role, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var channel = GetChannel(role);
        lock (channel.Sync)
        {
            channel.Subscribers++;
            // A zero-subscriber release cancels its loop immediately but that task may not have
            // observed cancellation yet. Keying restart only on IsCompleted left a new subscriber
            // holding a channel whose sole reconnect loop was already doomed to exit.
            if (_enabled
                && (channel.ReconnectCancellation is null
                    || channel.ReconnectCancellation.IsCancellationRequested))
            {
                StartReconnectLocked(channel);
            }
        }
        return ValueTask.FromResult<IAsyncDisposable>(new Subscription(this, channel));
    }

    /// <inheritdoc />
    public async Task<SteamUiEvaluationResult> EvaluateAsync(
        SteamUiTargetRole role,
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_enabled)
        {
            return SteamUiEvaluationResult.Unavailable(
                "Steam CEF integration disabled in settings.",
                Snapshot(GetChannel(role)).Generations);
        }
        await using IAsyncDisposable requestLease = await SubscribeAsync(role, cancellationToken)
            .ConfigureAwait(false);
        var channel = GetChannel(role);
        long ownershipGeneration = GetOwnershipGeneration(channel);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdown.Token);
            deadline.CancelAfter(timeout);
            var connection = await EnsureConnectedAsync(
                    channel,
                    ownershipGeneration,
                    deadline.Token)
                .ConfigureAwait(false);
            if (connection is null)
            {
                return SteamUiEvaluationResult.Unavailable(
                    Snapshot(channel).LastFailure ?? "Steam UI target is unavailable.",
                    Snapshot(channel).Generations);
            }

            var value = await connection.EvaluateAsync(expression, timeout, deadline.Token)
                .ConfigureAwait(false);
            var generations = Snapshot(channel).Generations;
            return new SteamUiEvaluationResult(true, value, null, generations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SteamUiEvaluationResult.Unavailable(
                "Steam UI evaluation was cancelled.", Snapshot(channel).Generations);
        }
        catch (OperationCanceledException)
        {
            return SteamUiEvaluationResult.Unavailable(
                "Steam UI evaluation timed out.", Snapshot(channel).Generations);
        }
        catch (InvalidDataException ex)
        {
            SetHealth(
                channel,
                ownershipGeneration,
                SteamUiTransportHealth.Incompatible,
                ex.Message);
            // Steam answered; its CDP reply or JavaScript result was incompatible. Preserve that
            // distinction so callers do not diagnose a renamed API as a closed Steam client.
            return new SteamUiEvaluationResult(
                true,
                null,
                ex.Message,
                Snapshot(channel).Generations);
        }
        catch (Exception ex)
        {
            SetHealth(
                channel,
                ownershipGeneration,
                SteamUiTransportHealth.Retrying,
                ex.Message);
            return SteamUiEvaluationResult.Unavailable(ex.Message, Snapshot(channel).Generations);
        }
    }

    /// <inheritdoc />
    public async Task SetRuntimeBindingAsync(
        SteamUiTargetRole role,
        string bindingName,
        bool installed,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingName);
        if (!_enabled)
        {
            throw new InvalidOperationException("Steam CEF integration is disabled in settings.");
        }
        await using IAsyncDisposable requestLease = await SubscribeAsync(role, cancellationToken)
            .ConfigureAwait(false);
        var channel = GetChannel(role);
        long ownershipGeneration = GetOwnershipGeneration(channel);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        deadline.CancelAfter(timeout);
        var connection = await EnsureConnectedAsync(
                channel,
                ownershipGeneration,
                deadline.Token)
            .ConfigureAwait(false)
            ?? throw new IOException("Steam UI target is unavailable.");
        _ = await connection.InvokeAsync(
                installed ? "Runtime.addBinding" : "Runtime.removeBinding",
                writer => writer.WriteString("name", bindingName),
                timeout,
                deadline.Token)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
        _channels.Values.Select(Snapshot).ToArray();

    /// <summary>Stops or resumes all CEF traffic while retaining subscriber intent.</summary>
    /// <param name="enabled">Whether repository-owned evaluations may reach Steam.</param>
    internal void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        foreach (TargetChannel channel in _channels.Values)
        {
            CancellationTokenSource? cancellation = null;
            SteamUiCdpConnection? connection = null;
            lock (channel.Sync)
            {
                if (enabled)
                {
                    if (channel.Subscribers > 0
                        && (channel.ReconnectCancellation is null
                            || channel.ReconnectCancellation.IsCancellationRequested))
                    {
                        StartReconnectLocked(channel);
                    }
                }
                else
                {
                    channel.OwnershipGeneration++;
                    cancellation = channel.ReconnectCancellation;
                    channel.ReconnectCancellation = null;
                    channel.ReconnectTask = null;
                    connection = channel.Connection;
                    channel.Connection = null;
                    channel.Health = SteamUiTransportHealth.Idle;
                }
            }
            cancellation?.Cancel();
            cancellation?.Dispose();
            if (connection is not null)
            {
                _ = DisposeDetachedConnectionAsync(channel.Role, connection);
            }
        }
    }

    private void StartReconnectLocked(TargetChannel channel)
    {
        channel.ReconnectCancellation?.Dispose();
        channel.ReconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token);
        long ownershipGeneration = ++channel.OwnershipGeneration;
        channel.ReconnectTask = ReconnectLoopAsync(
            channel,
            ownershipGeneration,
            channel.ReconnectCancellation.Token);
    }

    private async Task ReconnectLoopAsync(
        TargetChannel channel,
        long ownershipGeneration,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (_enabled && !cancellationToken.IsCancellationRequested)
        {
            SteamUiCdpConnection? connection;
            try
            {
                connection = await EnsureConnectedAsync(
                        channel,
                        ownershipGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetHealth(
                    channel,
                    ownershipGeneration,
                    SteamUiTransportHealth.Retrying,
                    ex.Message);
                connection = null;
            }

            if (connection is not null)
            {
                attempt = 0;
                try
                {
                    await connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                }
            }

            TimeSpan delay = RetryDelay(attempt);
            attempt++;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Returns the bounded reconnect delay for a zero-based failed attempt.</summary>
    internal static TimeSpan RetryDelay(int attempt) =>
        RetryDelays[Math.Clamp(attempt, 0, RetryDelays.Length - 1)];

    private async Task<SteamUiCdpConnection?> EnsureConnectedAsync(
        TargetChannel channel,
        long ownershipGeneration,
        CancellationToken cancellationToken)
    {
        lock (channel.Sync)
        {
            if (channel.Connection is not null)
            {
                return channel.Connection;
            }
            channel.Health = SteamUiTransportHealth.Connecting;
        }

        await channel.ConnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (channel.Sync)
            {
                if (channel.Connection is not null)
                {
                    return channel.Connection;
                }
            }

            var endpoint = await _discovery.DiscoverAsync(channel.Role, cancellationToken)
                .ConfigureAwait(false);
            if (endpoint is null)
            {
                SetHealth(
                    channel,
                    ownershipGeneration,
                    SteamUiTransportHealth.Unavailable,
                    $"Steam UI {channel.Role} target is absent.");
                return null;
            }

            var wire = await _wireFactory.ConnectAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            SteamUiCdpConnection? connection = null;
            var wireOwnedByConnection = false;
            try
            {
                lock (channel.Sync)
                {
                    if (Volatile.Read(ref _disposed) != 0
                        || channel.Subscribers == 0
                        || channel.OwnershipGeneration != ownershipGeneration)
                    {
                        Log.Info(
                            $"Steam UI {channel.Role} connection completed after its owner left; "
                            + "discarding it.");
                        throw new OperationCanceledException(
                            "The Steam UI channel owner changed while connecting.");
                    }
                }

                connection = new SteamUiCdpConnection(
                    endpoint,
                    wire,
                    (method, parameters) =>
                        OnNotification(channel, connection, method, parameters),
                    (closedConnection, failure) =>
                        OnConnectionClosed(channel, closedConnection, failure));
                connection.Start();
                wireOwnedByConnection = true;

                // Generation tracking depends on notifications. Runtime, Page and DOM events are
                // silent until their domains are enabled. Keep the candidate private until all
                // three calls succeed, so neither health nor GenerationChanged can claim a channel
                // ready while an in-place document replacement would still be invisible.
                await EnableGenerationDomainsAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
                lock (channel.Sync)
                {
                    // Recheck after enabling domains: the owner may have gone away while the CDP
                    // setup calls were in flight. Publishing here regardless would leave a live
                    // socket after the last subscription was released or CEF was disabled.
                    if (Volatile.Read(ref _disposed) != 0
                        || channel.Subscribers == 0
                        || channel.OwnershipGeneration != ownershipGeneration)
                    {
                        Log.Info(
                            $"Steam UI {channel.Role} connection completed after its owner left; "
                            + "discarding it.");
                        throw new OperationCanceledException(
                            "The Steam UI channel owner changed while connecting.");
                    }

                    var generations = channel.Generations;
                    if (!string.Equals(channel.BrowserId, endpoint.BrowserId, StringComparison.Ordinal))
                    {
                        channel.BrowserId = endpoint.BrowserId;
                        generations = generations with
                        {
                            Browser = generations.Browser + 1,
                            Target = generations.Target + 1,
                            Frame = generations.Frame + 1,
                            ExecutionContext = generations.ExecutionContext + 1,
                            Document = generations.Document + 1,
                        };
                    }
                    else if (!string.Equals(channel.TargetId, endpoint.TargetId, StringComparison.Ordinal))
                    {
                        generations = generations with
                        {
                            Target = generations.Target + 1,
                            Frame = generations.Frame + 1,
                            ExecutionContext = generations.ExecutionContext + 1,
                            Document = generations.Document + 1,
                        };
                    }
                    channel.TargetId = endpoint.TargetId;
                    channel.Generations = generations with
                    {
                        Session = generations.Session + 1,
                    };
                    channel.Connection = connection;
                    channel.Health = SteamUiTransportHealth.Ready;
                    channel.LastFailure = null;
                }
                RaiseGenerationChanged(channel);
                return connection;
            }
            catch
            {
                if (wireOwnedByConnection)
                {
                    lock (channel.Sync)
                    {
                        if (ReferenceEquals(channel.Connection, connection))
                        {
                            channel.Connection = null;
                        }
                    }
                    if (connection is not null)
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    await wire.DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            channel.ConnectGate.Release();
        }
    }

    private static async Task EnableGenerationDomainsAsync(
        SteamUiCdpConnection connection,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(5);
        await connection.InvokeAsync("Runtime.enable", null, timeout, cancellationToken)
            .ConfigureAwait(false);
        await connection.InvokeAsync("Page.enable", null, timeout, cancellationToken)
            .ConfigureAwait(false);
        await connection.InvokeAsync("DOM.enable", null, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private void OnNotification(
        TargetChannel channel,
        SteamUiCdpConnection? connection,
        string method,
        string parameters)
    {
        var changed = false;
        lock (channel.Sync)
        {
            // Ignore domain-enable chatter from a candidate that has not been published, plus any
            // final notification an already-detached socket races with its disposal. Only the
            // connection named by the current ownership generation may advance its generations.
            if (!ReferenceEquals(channel.Connection, connection))
            {
                return;
            }
            var generations = channel.Generations;
            switch (method)
            {
                case "Page.frameNavigated":
                    generations = generations with
                    {
                        Frame = generations.Frame + 1,
                        Document = generations.Document + 1,
                    };
                    changed = true;
                    break;
                case "Runtime.executionContextCreated":
                    generations = generations with
                    {
                        ExecutionContext = generations.ExecutionContext + 1,
                    };
                    changed = true;
                    break;
                case "Runtime.executionContextDestroyed":
                case "Runtime.executionContextsCleared":
                    generations = generations with
                    {
                        ExecutionContext = generations.ExecutionContext + 1,
                        Document = generations.Document + 1,
                    };
                    changed = true;
                    break;
                case "DOM.documentUpdated":
                    generations = generations with
                    {
                        Document = generations.Document + 1,
                    };
                    changed = true;
                    break;
            }
            channel.Generations = generations;
        }

        var snapshot = Snapshot(channel);
        NotificationReceived?.Invoke(this,
            new SteamUiNotification(channel.Role, method, parameters, snapshot.Generations));
        if (changed)
        {
            GenerationChanged?.Invoke(this, snapshot);
        }
    }

    private void OnConnectionClosed(
        TargetChannel channel, SteamUiCdpConnection connection, Exception? failure)
    {
        lock (channel.Sync)
        {
            if (!ReferenceEquals(channel.Connection, connection))
            {
                return;
            }
            channel.Connection = null;
            channel.Health = _enabled && channel.Subscribers > 0
                ? SteamUiTransportHealth.Retrying
                : SteamUiTransportHealth.Idle;
            channel.LastFailure = failure?.Message ?? "Steam UI target closed the channel.";
        }
    }

    private async ValueTask ReleaseAsync(TargetChannel channel)
    {
        SteamUiCdpConnection? connection = null;
        CancellationTokenSource? reconnectCancellation = null;
        lock (channel.Sync)
        {
            if (channel.Subscribers > 0)
            {
                channel.Subscribers--;
            }
            if (channel.Subscribers == 0)
            {
                channel.OwnershipGeneration++;
                reconnectCancellation = channel.ReconnectCancellation;
                channel.ReconnectCancellation = null;
                channel.ReconnectTask = null;
                connection = channel.Connection;
                channel.Connection = null;
                channel.Health = SteamUiTransportHealth.Idle;
            }
        }
        reconnectCancellation?.Cancel();
        reconnectCancellation?.Dispose();
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void SetHealth(
        TargetChannel channel,
        long ownershipGeneration,
        SteamUiTransportHealth health,
        string? failure)
    {
        lock (channel.Sync)
        {
            if (channel.OwnershipGeneration != ownershipGeneration)
            {
                return;
            }
            channel.Health = health;
            channel.LastFailure = Bound(failure, 1024);
        }
    }

    private static async Task DisposeDetachedConnectionAsync(
        SteamUiTargetRole role,
        SteamUiCdpConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Closing disabled Steam UI {role} channel failed: {ex.Message}");
        }
    }

    private void RaiseGenerationChanged(TargetChannel channel) =>
        GenerationChanged?.Invoke(this, Snapshot(channel));

    private static SteamUiTransportSnapshot Snapshot(TargetChannel channel)
    {
        lock (channel.Sync)
        {
            return new SteamUiTransportSnapshot(
                channel.Role,
                channel.Health,
                channel.Generations,
                channel.TargetId,
                channel.LastFailure,
                channel.Connection?.OutstandingRequests ?? 0,
                channel.Subscribers);
        }
    }

    private TargetChannel GetChannel(SteamUiTargetRole role) =>
        _channels.TryGetValue(role, out var channel)
            ? channel
            : throw new ArgumentOutOfRangeException(nameof(role));

    private static long GetOwnershipGeneration(TargetChannel channel)
    {
        lock (channel.Sync)
        {
            return channel.OwnershipGeneration;
        }
    }

    private static string? Bound(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength
            ? value
            : value[..maximumLength] + "...";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        foreach (var channel in _channels.Values)
        {
            SteamUiCdpConnection? connection;
            CancellationTokenSource? reconnectCancellation;
            lock (channel.Sync)
            {
                channel.OwnershipGeneration++;
                reconnectCancellation = channel.ReconnectCancellation;
                channel.ReconnectCancellation = null;
                connection = channel.Connection;
                channel.Connection = null;
                channel.Health = SteamUiTransportHealth.Disposed;
            }
            reconnectCancellation?.Cancel();
            reconnectCancellation?.Dispose();
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        _shutdown.Dispose();
    }

    private sealed class TargetChannel(SteamUiTargetRole role)
    {
        internal object Sync { get; } = new();

        internal SemaphoreSlim ConnectGate { get; } = new(1, 1);

        internal SteamUiTargetRole Role { get; } = role;

        internal SteamUiTransportHealth Health { get; set; } = SteamUiTransportHealth.Idle;

        internal SteamUiGenerations Generations { get; set; }

        internal string? BrowserId { get; set; }

        internal string? TargetId { get; set; }

        internal string? LastFailure { get; set; }

        internal SteamUiCdpConnection? Connection { get; set; }

        internal int Subscribers { get; set; }

        internal long OwnershipGeneration { get; set; }

        internal CancellationTokenSource? ReconnectCancellation { get; set; }

        internal Task? ReconnectTask { get; set; }
    }

    private sealed class Subscription(
        PersistentSteamUiTransport owner, TargetChannel channel) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0
                ? owner.ReleaseAsync(channel)
                : ValueTask.CompletedTask;
    }
}
