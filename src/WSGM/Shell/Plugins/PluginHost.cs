using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
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

    /// <summary>Reserves an instance identity without starting plugin code.</summary>
    /// <param name="plugin">The plugin retained by the returned registration until ordered stop and disposal.</param>
    /// <param name="identity">Its instance identity.</param>
    /// <param name="category">Its manifest category.</param>
    /// <param name="stateDirectory">The instance's state directory.</param>
    /// <param name="capabilities">
    ///     The capability channel of a <c>wsgm.gpu</c> instance, which the plugin reaches through
    ///     <see cref="IPluginHost.Capabilities" />. Required for that category and refused for any other.
    /// </param>
    /// <returns>An unstarted registration. Its slot remains reserved until stop confirms release and disposal succeeds.</returns>
    /// <exception cref="ArgumentException">
    ///     Identity, category, state path, or GPU channel is invalid; device packages use their own
    ///     runtime.
    /// </exception>
    /// <exception cref="InvalidOperationException">The identity is already reserved.</exception>
    internal PluginRegistration Admit(IPlugin plugin, PluginInstanceIdentity identity, string category,
        string stateDirectory, PluginCapabilityChannel? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (category == PluginCategories.Gpu != capabilities is not null
            || (capabilities is not null && capabilities.Identity != identity))
        {
            throw new ArgumentException(
                "A graphics plugin is admitted with its own capability channel, and no other category is.");
        }

        if (plugin.Id != identity.PluginId || string.IsNullOrWhiteSpace(identity.InstanceId)
                                           || string.IsNullOrWhiteSpace(category)
                                           || string.IsNullOrWhiteSpace(stateDirectory))
        {
            throw new ArgumentException("Plugin admission identity or context is invalid.");
        }

        if (category == PluginCategories.Device)
        {
            throw new ArgumentException("Device packages use the dedicated device runtime.");
        }

        lock (_gate)
        {
            if (_instances.ContainsKey(identity))
            {
                throw new InvalidOperationException("Plugin identity is already reserved.");
            }

            var registration = new PluginRegistration(this, plugin, identity,
                new PluginContext(identity, Generation: 1, _mode, Deadline.Never, stateDirectory), capabilities);
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
                || !PluginConfigurationRules.ValidKey(publication.Key))
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
