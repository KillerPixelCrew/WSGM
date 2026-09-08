using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Resident instance admission and generation-scoped health for every plugin category.</summary>
internal sealed class PluginHost(Action<Action> postToUi)
{
    private readonly object _gate = new();
    private readonly Dictionary<PluginInstanceIdentity, PluginRegistration> _instances = [];
    private PluginSessionMode _mode = PluginSessionMode.Desktop;
    private long _modeRevision;

    internal event Action<PluginHealthPublication>? HealthChanged;

    internal PluginRegistration Admit(IPlugin plugin, PluginInstanceIdentity identity, string category,
        PluginCategoryPolicy policy, bool selected, long generation, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (plugin.Id != identity.PluginId || string.IsNullOrWhiteSpace(identity.InstanceId)
            || string.IsNullOrWhiteSpace(category) || generation <= 0 || string.IsNullOrWhiteSpace(stateDirectory))
        { throw new ArgumentException("Plugin admission identity or context is invalid."); }
        if (category == PluginCategories.Device && policy != PluginCategoryPolicy.Device)
        { throw new ArgumentException("The Device category uses the selected singleton policy."); }
        if (policy.MinimumActive < 0 || policy.MaximumActive < policy.MinimumActive
            || policy.MaximumActive == 0 || (policy.RequiresSelection && !selected))
        { throw new InvalidOperationException("Plugin category policy does not admit this instance."); }
        lock (_gate)
        {
            var categoryInstances = _instances.Values.Where(instance => instance.Category == category).ToArray();
            if (_instances.ContainsKey(identity) || categoryInstances.Any(instance => instance.Policy != policy)
                || (policy.MaximumActive is { } maximum && categoryInstances.Length >= maximum))
            { throw new InvalidOperationException("Plugin identity or category slot is already reserved."); }
            var registration = new PluginRegistration(this, plugin, identity, category, policy,
                new(identity, generation, _mode, DateTimeOffset.MaxValue, stateDirectory));
            _instances.Add(identity, registration);
            return registration;
        }
    }

    internal PluginHealthPublication[] Snapshot()
    {
        lock (_gate) { return _instances.Values.Select(instance => instance.Health).ToArray(); }
    }

    internal async Task SetModeAsync(PluginSessionMode mode, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        PluginRegistration[] instances;
        long revision;
        lock (_gate)
        {
            _mode = mode;
            revision = ++_modeRevision;
            instances = _instances.Values.ToArray();
        }
        // Separate instance lanes prevent one unresponsive plugin from blocking another.
        await Task.WhenAll(instances.Select(instance => instance.SessionChangedAsync(mode, revision, deadline, cancellationToken)))
            .ConfigureAwait(false);
    }

    internal void Publish(PluginRegistration owner, PluginHealthPublication publication)
    {
        lock (_gate)
        {
            if (!IsCurrent(owner) || publication.Instance != owner.Identity
                || publication.Generation != owner.Context.Generation || owner.IsStopping
                || (owner.Quarantined && publication.Health != PluginHealth.Failed)
                || !Enum.IsDefined(publication.Health)) { return; }
            owner.Health = publication with { Detail = publication.Detail is { Length: > 2048 } detail ? detail[..2048] : publication.Detail };
        }
        postToUi(() =>
        {
            PluginHealthPublication current;
            lock (_gate)
            {
                if (!IsCurrent(owner) || owner.IsStopping || owner.Context.Generation != publication.Generation) { return; }
                current = owner.Health;
            }
            HealthChanged?.Invoke(current);
        });
    }

    internal void Retire(PluginRegistration owner)
    {
        lock (_gate)
        {
            if (IsCurrent(owner)) { _instances.Remove(owner.Identity); }
        }
    }

    private bool IsCurrent(PluginRegistration owner) =>
        _instances.TryGetValue(owner.Identity, out var current) && ReferenceEquals(current, owner);
}

/// <summary>Serial lifecycle lane. Timed-out work keeps ownership until it actually completes.</summary>
internal sealed class PluginRegistration(
    PluginHost host, IPlugin plugin, PluginInstanceIdentity identity, string category,
    PluginCategoryPolicy policy, PluginContext context) : IPluginHost
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _started;
    private bool _disposed;
    private bool? _released;
    private Exception? _stopFailure;
    private Exception? _disposeFailure;
    private long _modeRevision;
    private readonly object _modeGate = new();
    private CancellationTokenSource? _modeCancellation;
    internal PluginInstanceIdentity Identity { get; } = identity;
    internal string Category { get; } = category;
    internal PluginCategoryPolicy Policy { get; } = policy;
    internal PluginContext Context { get; private set; } = context;
    internal bool IsStopping { get; private set; }
    internal bool Quarantined { get; private set; }
    internal PluginHealthPublication Health { get; set; } = new(identity, context.Generation, PluginHealth.Unavailable, null);

    public void PublishHealth(PluginHealthPublication publication) => host.Publish(this, publication);

    internal Task<PluginHealth> StartAsync(DateTimeOffset deadline, CancellationToken cancellationToken) =>
        RunAsync(deadline, cancellationToken, async token =>
        {
            if (_started || IsStopping) { throw new InvalidOperationException("Plugin startup was already attempted."); }
            _started = true;
            var health = await plugin.StartAsync(this, Context, token).ConfigureAwait(false);
            PublishHealth(new(Identity, Context.Generation, health, null));
            return health;
        });

    internal async Task SessionChangedAsync(PluginSessionMode mode, long revision, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        CancellationTokenSource request;
        lock (_modeGate)
        {
            if (revision <= _modeRevision) { return; }
            _modeRevision = revision;
            if (_modeCancellation is { } previous)
            {
                _ = previous.CancelAsync().ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            _modeCancellation = request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        try
        {
            await RunAsync(deadline, request.Token, async token =>
            {
                Context = Context with { Mode = mode };
                if (_started && !IsStopping && !Quarantined) { await plugin.SessionChangedAsync(Context, token).ConfigureAwait(false); }
                return true;
            }, quarantineCancellation: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (_modeGate)
            {
                if (ReferenceEquals(_modeCancellation, request)) { _modeCancellation = null; }
                request.Dispose();
            }
        }
    }

    internal Task SuspendAsync(DateTimeOffset deadline, CancellationToken cancellationToken) =>
        RunAsync(deadline, cancellationToken, async token =>
        {
            RequireRunning();
            await plugin.SuspendAsync(Context, token).ConfigureAwait(false);
            return true;
        });

    internal Task ResumeAsync(long generation, DateTimeOffset deadline, CancellationToken cancellationToken) =>
        RunAsync(deadline, cancellationToken, async token =>
        {
            RequireRunning();
            if (generation <= Context.Generation) { throw new InvalidOperationException("Resume requires a fresh generation."); }
            Context = Context with { Generation = generation };
            PublishHealth(new(Identity, generation, PluginHealth.Unavailable, "Resuming"));
            await plugin.ResumeAsync(Context, token).ConfigureAwait(false);
            return true;
        });

    internal Task<bool> StopAsync(DateTimeOffset deadline, CancellationToken cancellationToken) =>
        RunAsync(deadline, cancellationToken, async token =>
        {
            IsStopping = true;
            Health = new(Identity, Context.Generation, PluginHealth.Unavailable, "Stopping");
            if (_stopFailure is not null) { throw new InvalidOperationException("Plugin stop previously failed; it will not be retried.", _stopFailure); }
            if (_released is { } released) { return released; }
            try { return (_released = await plugin.StopAsync(Context, token).ConfigureAwait(false)).Value; }
            catch (Exception ex) { _stopFailure = ex; throw; }
        });

    internal async ValueTask DisposeAsync()
    {
        await RunAsync(DateTimeOffset.UtcNow.AddSeconds(5), CancellationToken.None, async _ =>
        {
            if (_disposed) { return true; }
            if (!IsStopping) { throw new InvalidOperationException("Stop the plugin before disposing its registration."); }
            if (_disposeFailure is not null) { throw new InvalidOperationException("Plugin disposal previously failed.", _disposeFailure); }
            try { await plugin.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _disposeFailure = ex; throw; }
            _disposed = true;
            // Unconfirmed hardware release continues reserving its slot even after managed disposal.
            if (_released is true) { host.Retire(this); }
            return true;
        }, allowDisposed: true).ConfigureAwait(false);
    }

    private void RequireRunning()
    {
        if (!_started || IsStopping || Quarantined) { throw new InvalidOperationException("The plugin is not running."); }
    }

    private async Task<T> RunAsync<T>(DateTimeOffset deadline, CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> operation, bool allowDisposed = false, bool quarantineCancellation = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) { throw new TimeoutException("Plugin lifecycle deadline expired."); }
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(remaining);
        int operationEntered = 0;
        // The worker owns the budget and gate even if the caller's wait ends first. No disposal or
        // replacement can overtake plugin code that ignored cancellation.
        Task<T> work = Task.Run(async () =>
        {
            bool entered = false;
            try
            {
                await _lifecycle.WaitAsync(budget.Token).ConfigureAwait(false);
                entered = true;
                if (_disposed && !allowDisposed) { throw new ObjectDisposedException(nameof(PluginRegistration)); }
                Context = Context with { Deadline = deadline };
                Volatile.Write(ref operationEntered, 1);
                return await operation(budget.Token).ConfigureAwait(false);
            }
            finally
            {
                if (entered) { _lifecycle.Release(); }
                budget.Dispose();
            }
        }, CancellationToken.None);
        _ = work.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await work.WaitAsync(remaining, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (Volatile.Read(ref operationEntered) != 0 && (quarantineCancellation || ex is not OperationCanceledException))
            {
                Quarantined = true;
                PublishHealth(new(Identity, Context.Generation, PluginHealth.Failed, ex.Message));
            }
            throw;
        }
    }
}
