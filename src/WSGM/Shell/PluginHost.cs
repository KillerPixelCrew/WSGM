using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Resident instance admission and generation-scoped health for every plugin category.</summary>
/// <param name="postToUi">Queues a callback to the UI thread; publications are rechecked for freshness there.</param>
/// <param name="configurationStore">Desired-preference store shared by admitted registrations.</param>
internal sealed class PluginHost(Action<Action> postToUi, IPluginConfigurationStore configurationStore)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<PluginInstanceIdentity, PluginRegistration> _instances = [];
    private PluginSessionMode _mode = PluginSessionMode.Desktop;
    private long _modeRevision;

    /// <summary>The preference store used only for explicit saved edits and configuration restoration.</summary>
    internal IPluginConfigurationStore ConfigurationStore { get; } =
        configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));

    /// <summary>Accepted current-instance health notifications, delivered through the UI dispatcher.</summary>
    internal event Action<PluginHealthPublication>? HealthChanged;
    /// <summary>Accepted current-generation state changes, revalidated when delivered on the UI thread.</summary>
    internal event Action<PluginStatePublication>? StateChanged;

    /// <summary>Routes an explicit action to the admitted instance's serialized lifecycle lane.</summary>
    /// <param name="identity">Exact plugin and instance identity.</param>
    /// <param name="expectedGeneration">Generation seen by the caller; stale requests are refused before dispatch.</param>
    /// <param name="actionId">Declared action identifier.</param>
    /// <param name="arguments">Explicit argument values, captured before asynchronous work.</param>
    /// <param name="origin">Request source passed to the plugin.</param>
    /// <param name="deadline">Absolute budget for lane admission and execution; expiry does not abort native work.</param>
    /// <param name="cancellationToken">Cancels waiting and requests cooperative cancellation.</param>
    /// <returns>The correlated action result; uncertain effects are not retried.</returns>
    /// <exception cref="InvalidOperationException">The instance is not admitted, running, or at the expected generation.</exception>
    internal Task<PluginActionResult> InvokeActionAsync(PluginInstanceIdentity identity, long expectedGeneration,
        string actionId, IReadOnlyDictionary<string, PluginValue> arguments, PluginActionOrigin origin,
        Deadline deadline, CancellationToken cancellationToken)
    {
        PluginRegistration owner;
        lock (_gate)
        {
            if (!_instances.TryGetValue(identity, out owner!))
            {
                throw new InvalidOperationException("The plugin instance is not admitted.");
            }
        }

        return owner.InvokeActionAsync(expectedGeneration, actionId, arguments, origin, deadline, cancellationToken);
    }

    /// <summary>Reserves an instance identity and category slot without starting plugin code.</summary>
    /// <param name="plugin">The plugin retained by the returned registration until ordered stop and disposal.</param>
    /// <param name="identity">Its instance identity.</param>
    /// <param name="category">Its manifest category.</param>
    /// <param name="policy">The category's multiplicity policy.</param>
    /// <param name="selected">Whether the instance was explicitly selected.</param>
    /// <param name="generation">The first lifecycle generation.</param>
    /// <param name="stateDirectory">The instance's state directory.</param>
    /// <param name="capabilities">
    ///     The capability channel of a <c>wsgm.gpu</c> instance, which the plugin reaches through
    ///     <see cref="IPluginHost.Capabilities" />. Required for that category and refused for any other.
    /// </param>
    /// <returns>An unstarted registration. Its slot remains reserved until stop confirms release and disposal succeeds.</returns>
    /// <exception cref="ArgumentException">Identity, generation, category, state path, or GPU channel is invalid; device packages use their own runtime.</exception>
    /// <exception cref="InvalidOperationException">Selection/multiplicity policy or an existing reservation prevents admission.</exception>
    internal PluginRegistration Admit(IPlugin plugin, PluginInstanceIdentity identity, string category,
        PluginCategoryPolicy policy, bool selected, long generation, string stateDirectory,
        PluginCapabilityChannel? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (category == PluginCategories.Gpu != capabilities is not null
            || (capabilities is not null && capabilities.Identity != identity))
        {
            throw new ArgumentException(
                "A graphics plugin is admitted with its own capability channel, and no other category is.");
        }

        if (plugin.Id != identity.PluginId || string.IsNullOrWhiteSpace(identity.InstanceId)
                                           || string.IsNullOrWhiteSpace(category) || generation <= 0 ||
                                           string.IsNullOrWhiteSpace(stateDirectory))
        {
            throw new ArgumentException("Plugin admission identity or context is invalid.");
        }

        if (category == PluginCategories.Device)
        {
            throw new ArgumentException("Device packages use the dedicated device runtime.");
        }

        if (policy.MinimumActive < 0 || policy.MaximumActive < policy.MinimumActive
                                     || policy.MaximumActive == 0 || (policy.RequiresSelection && !selected))
        {
            throw new InvalidOperationException("Plugin category policy does not admit this instance.");
        }

        lock (_gate)
        {
            var categoryInstances = _instances.Values.Where(instance => instance.Category == category).ToArray();
            if (_instances.ContainsKey(identity) || categoryInstances.Any(instance => instance.Policy != policy)
                                                 || (policy.MaximumActive is { } maximum &&
                                                     categoryInstances.Length >= maximum))
            {
                throw new InvalidOperationException("Plugin identity or category slot is already reserved.");
            }

            var registration = new PluginRegistration(this, plugin, identity, category, policy,
                new PluginContext(identity, generation, _mode, Deadline.Never, stateDirectory), capabilities);
            _instances.Add(identity, registration);
            return registration;
        }
    }

    /// <summary>Captures the last accepted health of every currently reserved instance.</summary>
    /// <returns>A new array; failed or stopping registrations remain present until safely retired.</returns>
    internal PluginHealthPublication[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _instances.Values.Select(instance => instance.Health)];
        }
    }

    /// <summary>Captures the newest accepted value of each state key in an instance's current generation.</summary>
    /// <param name="identity">Admitted instance to inspect.</param>
    /// <returns>A new array, or an empty array when absent or no current-generation state has been published.</returns>
    internal PluginStatePublication[] StateSnapshot(PluginInstanceIdentity identity)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(identity, out var owner)
                ? [.. owner.State.Values.Where(state => state.Generation == owner.Context.Generation)]
                : [];
        }
    }

    /// <summary>Accepts valid, strictly increasing state publications from the current nonstopping registration.</summary>
    /// <param name="owner">Registration that supplied the publication; replaced or quarantined owners are ignored.</param>
    /// <param name="publication">Current-generation state with a sequence increasing across all keys.</param>
    /// <remarks>Invalid/stale values are discarded. Accepted state never writes desired preferences.</remarks>
    internal void PublishState(PluginRegistration owner, PluginStatePublication publication)
    {
        lock (_gate)
        {
            if (!IsCurrent(owner) || publication.Instance != owner.Identity || owner.IsStopping || owner.Quarantined
                || publication.Generation != owner.Context.Generation || publication.Sequence <= 0
                || !publication.Value.IsValid || !Enum.IsDefined(publication.Origin) ||
                publication.ConfigurationRevision < 0
                || string.IsNullOrEmpty(publication.Key)
                || publication.Key.Any(character =>
                    character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')))
            {
                return;
            }

            if (owner.StateGeneration != publication.Generation)
            {
                owner.State.Clear();
                owner.StateGeneration = publication.Generation;
                owner.StateSequence = 0;
            }

            if (publication.Sequence <= owner.StateSequence)
            {
                return;
            }

            owner.StateSequence = publication.Sequence;
            owner.State[publication.Key] = publication;
        }

        postToUi(() =>
        {
            lock (_gate)
            {
                if (!IsCurrent(owner) || owner.IsStopping || owner.Quarantined
                    || owner.Context.Generation != publication.Generation
                    || !owner.State.TryGetValue(publication.Key, out var current) || current != publication)
                {
                    return;
                }
            }

            StateChanged?.Invoke(publication);
        });
    }

    /// <summary>Advances the host mode revision and notifies admitted instances on their independent lanes.</summary>
    /// <param name="mode">New mode inherited by later admissions.</param>
    /// <param name="deadline">Shared absolute budget for all notifications.</param>
    /// <param name="cancellationToken">Cancels waits and requests cooperative cancellation in each instance.</param>
    /// <returns>Completion of all notifications; one failure does not prevent the other lanes from running.</returns>
    internal async Task SetModeAsync(PluginSessionMode mode, Deadline deadline,
        CancellationToken cancellationToken)
    {
        PluginRegistration[] instances;
        long revision;
        lock (_gate)
        {
            _mode = mode;
            revision = ++_modeRevision;
            instances = [.. _instances.Values];
        }

        // Separate instance lanes prevent one unresponsive plugin from blocking another.
        await Task.WhenAll(instances.Select(instance =>
                instance.SessionChangedAsync(mode, revision, deadline, cancellationToken)))
            .ConfigureAwait(false);
    }

    /// <summary>Records that one instance is stopping, under the lock every snapshot reads.</summary>
    /// <param name="owner">The registration being stopped.</param>
    /// <remarks>
    ///     Not <see cref="Publish" />: that refuses a stopping instance's publications, and this is the host's
    ///     own record of the stop.
    /// </remarks>
    internal void MarkStopping(PluginRegistration owner)
    {
        lock (_gate)
        {
            owner.Health = new PluginHealthPublication(owner.Identity, owner.Context.Generation,
                PluginHealth.Unavailable, "Stopping");
        }
    }

    /// <summary>Records valid current-generation health and dispatches a UI notification when it changes.</summary>
    /// <param name="owner">Current registration; retired or stopping owners are ignored.</param>
    /// <param name="publication">Health for that identity and generation; quarantined instances may only publish failure.</param>
    internal void Publish(PluginRegistration owner, PluginHealthPublication publication)
    {
        lock (_gate)
        {
            if (!IsCurrent(owner) || publication.Instance != owner.Identity
                                  || publication.Generation != owner.Context.Generation || owner.IsStopping
                                  || (owner.Quarantined && publication.Health != PluginHealth.Failed)
                                  || !Enum.IsDefined(publication.Health))
            {
                return;
            }

            // A plugin that republishes unchanged health on every observation raises nothing.
            if (owner.Health == publication)
            {
                return;
            }

            owner.Health = publication;
        }

        postToUi(() =>
        {
            PluginHealthPublication current;
            lock (_gate)
            {
                if (!IsCurrent(owner) || owner.IsStopping || owner.Context.Generation != publication.Generation)
                {
                    return;
                }

                current = owner.Health;
            }

            HealthChanged?.Invoke(current);
        });
    }

    /// <summary>Releases the reservation only when it is still owned by this exact registration.</summary>
    /// <param name="owner">Registration whose confirmed release and disposal permit retirement.</param>
    internal void Retire(PluginRegistration owner)
    {
        lock (_gate)
        {
            if (IsCurrent(owner))
            {
                _instances.Remove(owner.Identity);
            }
        }
    }

    private bool IsCurrent(PluginRegistration owner)
    {
        return _instances.TryGetValue(owner.Identity, out var current) && ReferenceEquals(current, owner);
    }
}

/// <summary>Serial lifecycle lane. Timed-out work keeps ownership until it actually completes.</summary>
/// <param name="host">Admission and publication owner; retained until this registration retires.</param>
/// <param name="plugin">Plugin whose lifecycle and actions are serialized on this registration's lane.</param>
/// <param name="identity">Stable plugin/instance identity for this reservation.</param>
/// <param name="category">Manifest category already accepted by the host.</param>
/// <param name="policy">Multiplicity and selection policy under which the slot was reserved.</param>
/// <param name="context">Initial identity, generation, mode, and instance state directory.</param>
/// <param name="capabilities">Owned GPU publication/command channel, or null for other categories.</param>
internal sealed class PluginRegistration(
    PluginHost host,
    IPlugin plugin,
    PluginInstanceIdentity identity,
    string category,
    PluginCategoryPolicy policy,
    PluginContext context,
    PluginCapabilityChannel? capabilities = null) : IPluginHost
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _modeGate = new();
    private CancellationTokenSource? _activeCancellation;
    private Exception? _disposeFailure;
    private bool _disposed;
    private CancellationTokenSource? _modeCancellation;
    private long _modeRevision;
    private bool? _released;
    private bool _started;
    private bool _stopAttempted;
    private Exception? _stopFailure;
    private int _stopRequested;
    /// <summary>Stable identity of the reserved instance.</summary>
    internal PluginInstanceIdentity Identity { get; } = identity;
    /// <summary>Admitted manifest category used for multiplicity accounting.</summary>
    internal string Category { get; } = category;
    /// <summary>Category policy that must match every other reservation in this category.</summary>
    internal PluginCategoryPolicy Policy { get; } = policy;
    /// <summary>Current context, updated on the serial lane before each lifecycle operation.</summary>
    internal PluginContext Context { get; private set; } = context;
    /// <summary>Whether admission has closed; queued actions and settings may no longer dispatch.</summary>
    internal bool IsStopping => Volatile.Read(ref _stopRequested) != 0;
    /// <summary>Whether uncertain lifecycle or frontend failure prevents further running operations.</summary>
    internal bool Quarantined { get; private set; }

    /// <summary>The last accepted health; written only by the host under its lock.</summary>
    internal PluginHealthPublication Health { get; set; } =
        new(identity, context.Generation, PluginHealth.Unavailable, null);

    /// <summary>Latest accepted state by key; mutable only while holding the host lock.</summary>
    internal Dictionary<string, PluginStatePublication> State { get; } = new(StringComparer.Ordinal);

    /// <summary>The generation of <see cref="State" />; written only by the host under its lock.</summary>
    internal long StateGeneration { get; set; }

    /// <summary>The last accepted state sequence; written only by the host under its lock.</summary>
    internal long StateSequence { get; set; }

    /// <summary>Captured configurable-plugin settings, or null before startup or when unsupported.</summary>
    internal CommonPluginSettings? Settings { get; private set; }

    /// <summary>Validated action/UI declarations, or null before startup declaration capture.</summary>
    internal CommonPluginActions? Actions { get; private set; }

    /// <summary>The capability channel of a graphics plugin, or null.</summary>
    internal PluginCapabilityChannel? CapabilityChannel => capabilities;

    /// <inheritdoc />
    /// <remarks>
    ///     Each start and resume begins a new capability cycle before the plugin runs, so what it publishes
    ///     from inside that call is already current.
    /// </remarks>
    public ICapabilityHost? Capabilities => capabilities;

    /// <inheritdoc />
    public void PublishHealth(PluginHealthPublication publication)
    {
        host.Publish(this, publication);
    }

    /// <inheritdoc />
    public void PublishState(PluginStatePublication publication)
    {
        host.PublishState(this, publication);
    }

    /// <inheritdoc />
    public void Trace(DeviceTraceLevel level, string scope, string message)
    {
        if (!_disposed)
        {
            PluginLogLine.Write("plugin/" + Identity.PluginId, level, scope, message);
        }
    }

    /// <inheritdoc />
    public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
    {
        if (!_disposed)
        {
            PluginLogLine.WriteChange("plugin/" + Identity.PluginId, level, scope, key, message);
        }
    }

    /// <summary>Refuses further running operations and reports failure after invalid frontend behavior.</summary>
    /// <param name="detail">Diagnostic reason for quarantine.</param>
    internal void QuarantineFrontend(string detail)
    {
        Quarantined = true;
        PublishHealth(new PluginHealthPublication(Identity, Context.Generation, PluginHealth.Failed, detail));
    }

    /// <summary>Starts once after capturing declarations, then restores desired settings on the same lane.</summary>
    /// <param name="deadline">Budget covering queue admission, startup, and configuration restoration.</param>
    /// <param name="cancellationToken">Cancels waiting and requests cancellation; unfinished work retains lane ownership.</param>
    /// <returns>Startup health after restoration was attempted; configuration has its own result.</returns>
    /// <exception cref="InvalidOperationException">Startup was already attempted or admission has closed.</exception>
    internal Task<PluginHealth> StartAsync(Deadline deadline, CancellationToken cancellationToken)
    {
        return RunAsync(deadline, async token =>
        {
            if (_started || IsStopping)
            {
                throw new InvalidOperationException("Plugin startup was already attempted.");
            }

            _started = true;
            Actions = new CommonPluginActions(plugin);
            if (plugin is IConfigurablePlugin configurable)
            {
                Settings = new CommonPluginSettings(configurable, host.ConfigurationStore, Identity);
            }

            capabilities?.BeginCycle(Context.Generation);
            var health = await plugin.StartAsync(this, Context, token).ConfigureAwait(false);
            if (Settings is not null)
            {
                await Settings.RestoreAsync(Context, token).ConfigureAwait(false);
            }

            PublishHealth(new PluginHealthPublication(Identity, Context.Generation, health, null));
            return health;
        }, cancellationToken: cancellationToken);
    }

    /// <summary>Delivers only a newly saved preference revision to a running configurable plugin.</summary>
    /// <param name="deadline">Lane and delivery budget.</param>
    /// <param name="cancellationToken">Cancels waiting and requests cooperative delivery cancellation.</param>
    /// <returns>The existing result for an unchanged revision, or the new delivery result.</returns>
    internal Task<PluginConfigurationResult> RefreshConfigurationAsync(Deadline deadline,
        CancellationToken cancellationToken)
    {
        return RunAsync(deadline, async token =>
        {
            RequireRunning();
            if (Settings is null)
            {
                throw new InvalidOperationException("The plugin does not declare settings.");
            }

            return await Settings.RefreshAsync(Context, token).ConfigureAwait(false);
        }, quarantineFailure: false, cancellationToken: cancellationToken);
    }

    /// <summary>Captures action arguments and validates the expected generation on the serialized lane.</summary>
    /// <param name="expectedGeneration">Generation used to render the action; a stale generation is refused.</param>
    /// <param name="actionId">Declared action identifier.</param>
    /// <param name="arguments">Explicit argument values copied before queueing.</param>
    /// <param name="origin">Request source passed to the plugin.</param>
    /// <param name="deadline">Budget for lane admission and one action dispatch.</param>
    /// <param name="cancellationToken">Cancels waiting; a dispatched effect may remain unconfirmed.</param>
    /// <returns>The correlated result, without automatically retrying rejected or uncertain actions.</returns>
    internal Task<PluginActionResult> InvokeActionAsync(long expectedGeneration, string actionId,
        IReadOnlyDictionary<string, PluginValue> arguments, PluginActionOrigin origin,
        Deadline deadline, CancellationToken cancellationToken)
    {
        var captured = new Dictionary<string, PluginValue>(arguments, StringComparer.Ordinal);
        return RunAsync(deadline, async token =>
        {
            RequireRunning();
            if (expectedGeneration != Context.Generation || Actions is null)
            {
                throw new InvalidOperationException("Action generation is stale or the plugin has not started.");
            }

            return await Actions.ExecuteAsync(actionId, origin, captured, Context, token).ConfigureAwait(false);
        }, quarantineFailure: false, cancellationToken: cancellationToken);
    }

    /// <summary>Serializes an explicit preference edit, saving desired values before one delivery attempt.</summary>
    /// <param name="expectedRevision">Saved preference revision the editor observed.</param>
    /// <param name="changes">Declared changes copied before queueing.</param>
    /// <param name="deadline">Budget for lane admission and delivery.</param>
    /// <param name="cancellationToken">Cancels waiting/delivery; preferences already saved remain saved.</param>
    /// <returns>The delivery confirmation; rejection or uncertainty does not roll back desired preferences.</returns>
    internal Task<PluginConfigurationResult> ConfigureAsync(long expectedRevision,
        IReadOnlyDictionary<string, PluginValue> changes,
        Deadline deadline, CancellationToken cancellationToken)
    {
        // Capture caller-owned mutable UI data before queueing work.
        var captured = new Dictionary<string, PluginValue>(changes, StringComparer.Ordinal);
        return RunAsync(deadline, async token =>
        {
            RequireRunning();
            if (Settings is null)
            {
                throw new InvalidOperationException("The plugin does not declare settings.");
            }

            return await Settings.ChangeAsync(expectedRevision, captured, Context, token).ConfigureAwait(false);
        }, quarantineFailure: false, cancellationToken: cancellationToken);
    }

    /// <summary>Applies a newer mode revision, canceling superseded mode requests.</summary>
    /// <param name="mode">Desired host session mode.</param>
    /// <param name="revision">Monotonically increasing host revision; old/equal requests are ignored.</param>
    /// <param name="deadline">Budget for lane admission and plugin notification.</param>
    /// <param name="cancellationToken">Caller cancellation, which propagates; cancellation by supersession is consumed.</param>
    /// <returns>Completion after context update and any permitted plugin notification.</returns>
    internal async Task SessionChangedAsync(PluginSessionMode mode, long revision, Deadline deadline,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource request;
        lock (_modeGate)
        {
            if (revision <= _modeRevision)
            {
                return;
            }

            _modeRevision = revision;
            if (_modeCancellation is { } previous)
            {
                previous.CancelAsync().ObserveFaults();
            }

            _modeCancellation = request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        try
        {
            await RunAsync(deadline, async token =>
            {
                Context = Context with { Mode = mode };
                if (_started && !IsStopping && !Quarantined)
                {
                    await plugin.SessionChangedAsync(Context, token).ConfigureAwait(false);
                }

                return true;
            }, quarantineCancellation: false, cancellationToken: request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_modeGate)
            {
                if (ReferenceEquals(_modeCancellation, request))
                {
                    _modeCancellation = null;
                }

                request.Dispose();
            }
        }
    }

    /// <summary>Closes GPU capability admission before suspending a running plugin.</summary>
    /// <param name="deadline">Budget for lane admission and suspension.</param>
    /// <param name="cancellationToken">Cancels waiting and requests cooperative suspension cancellation.</param>
    /// <returns>Plugin suspension completion; uncertain unfinished work retains the lane.</returns>
    internal Task SuspendAsync(Deadline deadline, CancellationToken cancellationToken)
    {
        return RunAsync(deadline, async token =>
        {
            RequireRunning();
            capabilities?.Suspend();
            await plugin.SuspendAsync(Context, token).ConfigureAwait(false);
            return true;
        }, cancellationToken: cancellationToken);
    }

    /// <summary>Begins a fresh generation and capability cycle before notifying the plugin to resume.</summary>
    /// <param name="generation">Generation strictly greater than the current one.</param>
    /// <param name="deadline">Budget for lane admission and resume.</param>
    /// <param name="cancellationToken">Cancels waiting and requests cooperative resume cancellation.</param>
    /// <returns>Resume completion; the plugin publishes its resulting health separately.</returns>
    internal Task ResumeAsync(long generation, Deadline deadline, CancellationToken cancellationToken)
    {
        return RunAsync(deadline, async token =>
        {
            RequireRunning();
            if (generation <= Context.Generation)
            {
                throw new InvalidOperationException("Resume requires a fresh generation.");
            }

            Context = Context with { Generation = generation };
            PublishHealth(new PluginHealthPublication(Identity, generation, PluginHealth.Unavailable, "Resuming"));
            capabilities?.BeginCycle(generation);
            await plugin.ResumeAsync(Context, token).ConfigureAwait(false);
            return true;
        }, cancellationToken: cancellationToken);
    }

    /// <summary>Refuses directly queued settings and actions without waiting for plugin stop.</summary>
    internal void CloseAdmission()
    {
        var active = Volatile.Read(ref _activeCancellation);
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        if (active is not null)
        {
            CancelQuietly(active);
        }

        // No new command or sync reaches a plugin that is being stopped.
        capabilities?.Suspend();
    }

    /// <summary>Closes admission and attempts plugin stop once, including after incomplete startup.</summary>
    /// <param name="deadline">Budget for lane admission and stop.</param>
    /// <param name="cancellationToken">Cancels waiting and requests stop cancellation; it cannot prove native release.</param>
    /// <returns>Whether the plugin confirmed release. Repeated calls reuse that result rather than rerunning stop.</returns>
    /// <remarks>A previous stop exception is retained and reported again; no uncertain stop is automatically retried.</remarks>
    internal Task<bool> StopAsync(Deadline deadline, CancellationToken cancellationToken)
    {
        try
        {
            CloseAdmission();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Plugin admission closure failed; ordered stop continues: {ex.Message}");
        }

        return RunAsync(deadline, async token =>
        {
            _stopAttempted = true;
            host.MarkStopping(this);
            if (_stopFailure is not null)
            {
                throw new InvalidOperationException("Plugin stop previously failed; it will not be retried.",
                    _stopFailure);
            }

            if (_released is { } released)
            {
                return released;
            }

            try
            {
                return (_released = await plugin.StopAsync(Context, token).ConfigureAwait(false)).Value;
            }
            catch (Exception ex)
            {
                _stopFailure = ex;
                throw;
            }
        }, cancellationToken: cancellationToken);
    }

    /// <summary>Disposes after a stop attempt, using a five-second wait budget without overtaking unfinished work.</summary>
    /// <returns>Disposal completion. The slot is retired only when stop also confirmed resource release.</returns>
    /// <exception cref="InvalidOperationException">Stop was not attempted, or an earlier disposal failed.</exception>
    /// <remarks>A timeout may leave disposal running on the retained lane; a failed disposal is not repeated.</remarks>
    internal async ValueTask DisposeAsync()
    {
        await RunAsync(Deadline.After(TimeSpan.FromSeconds(5)), async _ =>
        {
            if (_disposed)
            {
                return true;
            }

            if (!_stopAttempted)
            {
                throw new InvalidOperationException("Stop the plugin before disposing its registration.");
            }

            if (_disposeFailure is not null)
            {
                throw new InvalidOperationException("Plugin disposal previously failed.", _disposeFailure);
            }

            try
            {
                await plugin.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _disposeFailure = ex;
                throw;
            }

            _disposed = true;
            // A plugin keeps its reservation until it confirms release.
            if (_released is true)
            {
                host.Retire(this);
            }

            return true;
        }, true).ConfigureAwait(false);
    }

    private static void CancelQuietly(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.CancelAsync().ObserveFaults();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RequireRunning()
    {
        if (!_started || IsStopping || Quarantined)
        {
            throw new InvalidOperationException("The plugin is not running.");
        }
    }

    private async Task<T> RunAsync<T>(Deadline deadline, Func<CancellationToken, Task<T>> operation,
        bool allowDisposed = false, bool quarantineCancellation = true, bool quarantineFailure = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("Plugin lifecycle deadline expired.");
        }

        using var waitBudget = deadline.CreateCancellationSource(cancellationToken);
        var budget = deadline.CreateCancellationSource(cancellationToken);
        var budgetToken = budget.Token;
        StrongBox<int> operationEntered = new();
        // The worker owns the budget and gate even if the caller's wait ends first. No disposal or
        // replacement can overtake plugin code that ignored cancellation.
        var work = Task.Run(async () =>
        {
            var entered = false;
            try
            {
                await _lifecycle.WaitAsync(budgetToken).ConfigureAwait(false);
                entered = true;
                ObjectDisposedException.ThrowIf(_disposed && !allowDisposed, this);
                Context = Context with { Deadline = deadline };
                Interlocked.Exchange(ref _activeCancellation, budget);
                Volatile.Write(ref operationEntered.Value, 1);
                return await operation(budgetToken).ConfigureAwait(false);
            }
            finally
            {
                if (entered)
                {
                    Interlocked.CompareExchange(ref _activeCancellation, null, budget);
                    _lifecycle.Release();
                }

                budget.Dispose();
            }
        }, CancellationToken.None);
        work.ObserveFaults();
        try
        {
            return await work.WaitAsync(waitBudget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var failure = ex is OperationCanceledException && !cancellationToken.IsCancellationRequested
                                                           && deadline.HasExpired
                ? new TimeoutException("Plugin lifecycle deadline expired.", ex)
                : ex;
            if (!quarantineFailure || Volatile.Read(ref operationEntered.Value) == 0
                                   || (!quarantineCancellation && failure is OperationCanceledException))
            {
                if (!ReferenceEquals(failure, ex))
                {
                    throw failure;
                }

                throw;
            }

            Quarantined = true;
            PublishHealth(new PluginHealthPublication(Identity, Context.Generation, PluginHealth.Failed,
                failure.Message));
            if (!ReferenceEquals(failure, ex))
            {
                throw failure;
            }

            throw;
        }
    }
}
