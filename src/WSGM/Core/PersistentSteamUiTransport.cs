using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
            if (channel.ReconnectTask is null || channel.ReconnectTask.IsCompleted)
            {
                channel.ReconnectCancellation?.Dispose();
                channel.ReconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdown.Token);
                channel.ReconnectTask = ReconnectLoopAsync(
                    channel, channel.ReconnectCancellation.Token);
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
        var channel = GetChannel(role);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdown.Token);
            deadline.CancelAfter(timeout);
            var connection = await EnsureConnectedAsync(channel, deadline.Token)
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
            SetHealth(channel, SteamUiTransportHealth.Incompatible, ex.Message);
            return SteamUiEvaluationResult.Unavailable(ex.Message, Snapshot(channel).Generations);
        }
        catch (Exception ex)
        {
            SetHealth(channel, SteamUiTransportHealth.Retrying, ex.Message);
            return SteamUiEvaluationResult.Unavailable(ex.Message, Snapshot(channel).Generations);
        }
    }

    internal async Task<JsonElement> InvokeCdpAsync(
        SteamUiTargetRole role,
        string method,
        Action<Utf8JsonWriter>? writeParameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var channel = GetChannel(role);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        deadline.CancelAfter(timeout);
        var connection = await EnsureConnectedAsync(channel, deadline.Token)
            .ConfigureAwait(false)
            ?? throw new IOException("Steam UI target is unavailable.");
        return await connection.InvokeAsync(method, writeParameters, timeout, deadline.Token)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
        _channels.Values.Select(Snapshot).ToArray();

    private async Task ReconnectLoopAsync(TargetChannel channel, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SteamUiCdpConnection? connection;
            try
            {
                connection = await EnsureConnectedAsync(channel, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetHealth(channel, SteamUiTransportHealth.Retrying, ex.Message);
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

            var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
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

    private async Task<SteamUiCdpConnection?> EnsureConnectedAsync(
        TargetChannel channel, CancellationToken cancellationToken)
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
                SetHealth(channel, SteamUiTransportHealth.Unavailable,
                    $"Steam UI {channel.Role} target is absent.");
                return null;
            }

            var wire = await _wireFactory.ConnectAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                SteamUiCdpConnection connection;
                lock (channel.Sync)
                {
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
                    connection = new SteamUiCdpConnection(
                        endpoint,
                        wire,
                        (method, parameters) => OnNotification(channel, method, parameters),
                        (closedConnection, failure) =>
                            OnConnectionClosed(channel, closedConnection, failure));
                    channel.Connection = connection;
                    channel.Health = SteamUiTransportHealth.Ready;
                    channel.LastFailure = null;
                    connection.Start();
                }
                RaiseGenerationChanged(channel);
                return connection;
            }
            catch
            {
                await wire.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            channel.ConnectGate.Release();
        }
    }

    private void OnNotification(TargetChannel channel, string method, string parameters)
    {
        var changed = false;
        lock (channel.Sync)
        {
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
            channel.Health = channel.Subscribers > 0
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
                reconnectCancellation = channel.ReconnectCancellation;
                channel.ReconnectCancellation = null;
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
        TargetChannel channel, SteamUiTransportHealth health, string? failure)
    {
        lock (channel.Sync)
        {
            channel.Health = health;
            channel.LastFailure = Bound(failure, 1024);
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
