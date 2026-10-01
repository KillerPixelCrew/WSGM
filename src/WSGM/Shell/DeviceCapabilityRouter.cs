using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>Stable dictionary key for one semantic capability instance.</summary>
internal readonly record struct DeviceCapabilityKey(string CapabilityId, string? InstanceId)
{
    public override string ToString()
    {
        return InstanceId is { Length: > 0 }
            ? $"{CapabilityId}#{InstanceId}"
            : CapabilityId;
    }
}

/// <summary>One immutable router snapshot suitable for an overlay or diagnostics client.</summary>
/// <param name="Descriptor">The capability.</param>
/// <param name="Projection">Its state, desired value and command progress.</param>
/// <param name="LastResult">How the last command finished.</param>
/// <param name="LastCommandValue">The value that command wrote, or null for an action or none.</param>
internal sealed record DeviceCapabilityView(
    CapabilityDescriptor Descriptor,
    CapabilityProjection Projection,
    CapabilityCommandResult? LastResult,
    CapabilityValue? LastCommandValue = null)
{
    /// <summary>
    ///     The publisher's profile key, such as <c>gpu:wsgm.gpu.intel</c>, or null for the device package.
    /// </summary>
    public string? Publisher { get; init; }

    /// <summary>The setting id this capability's stored value is addressed by.</summary>
    public ProfileSettingKey SettingKey =>
        ProfileSettingKey.ForDevice(Descriptor.CapabilityId, Descriptor.InstanceId, Publisher);
}

/// <summary>
///     Validates and projects the semantic capability stream owned by one plugin generation.
/// </summary>
/// <remarks>
///     One router per publisher: the device package has one, and so does each graphics package. They are
///     never merged, because consumers that select a capability by role expect exactly one match within
///     the publisher they read.
/// </remarks>
internal sealed class DeviceCapabilityRouter : IAsyncDisposable
{
    /// <summary>Last logged availability per capability, so only changes are written.</summary>
    private readonly Dictionary<DeviceCapabilityKey, bool> _availability = [];

    private readonly Dictionary<DeviceCapabilityKey, SemaphoreSlim> _commandGates = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityDescriptor> _descriptors = [];
    private readonly Lock _gate = new();

    /// <summary>The name log lines carry: "Device", or the graphics publisher's profile key.</summary>
    private readonly string _label;

    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _lastCommandValues = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityCommandResult> _lastResults = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _pendingValues = [];

    private readonly Action<Action> _postToUi;

    private readonly Action _publishPosted;

    /// <summary>The publisher's profile key, or null for the device package.</summary>
    private readonly string? _publisher;

    /// <summary>Latest accepted state per capability.</summary>
    /// <remarks>
    ///     The high-rate state channel does not promise ordering, and a delayed older sample overwriting
    ///     a newer one is not cosmetic: it can restore a "fresh" reading the device has already moved
    ///     past, and the UI would then command against it. Sequence numbers are per cycle generation, so
    ///     stale-generation publications are refused by validation before they reach this map.
    /// </remarks>
    private readonly Dictionary<DeviceCapabilityKey, CapabilityStateDelta> _states = [];

    private ICapabilityPublisher? _client;
    private bool _connected;
    private long _cycleGeneration;

    private long _descriptorGeneration;
    private bool _disposed;

    // The stored profile values per capability instance, one index per layer, rebuilt when the profiles or
    // the running application change rather than on every snapshot a state delta builds. Kept apart so
    // each descriptor's profile scope decides which layers it resolves from.
    private Dictionary<DeviceCapabilityKey, CapabilityValue> _gameDesired = [];
    private Dictionary<DeviceCapabilityKey, CapabilityValue> _globalDesired = [];
    private bool _onAcPower = true;

    // The same descriptors in snapshot order, sorted once per descriptor set rather than on every
    // snapshot a state delta builds.
    private KeyValuePair<DeviceCapabilityKey, CapabilityDescriptor>[] _orderedDescriptors = [];
    private bool _publishPending;

    /// <summary>Overlay sections of the accepted descriptor set, replaced with each set.</summary>
    private IReadOnlyList<CapabilitySection> _sections = [];

    /// <param name="postToUi">Posts the projection build to the UI dispatcher.</param>
    /// <param name="publisher">A graphics publisher's profile key, or null for the device package.</param>
    internal DeviceCapabilityRouter(Action<Action> postToUi, string? publisher = null)
    {
        ArgumentNullException.ThrowIfNull(postToUi);
        _postToUi = postToUi;
        _publisher = publisher;
        _label = publisher ?? "Device";
        _publishPosted = PublishPosted;
    }

    /// <summary>The publisher's profile key, or null for the device package.</summary>
    internal string? Publisher => _publisher;

    /// <summary>The declared overlay sections of the accepted descriptor set.</summary>
    internal IReadOnlyList<CapabilitySection> Sections
    {
        get
        {
            lock (_gate)
            {
                return _sections;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            _disposed = true;
            DetachUnderGate();
            // An admitted ExecuteAsync releases its local gate in finally. Clearing the index
            // blocks reuse without disposing a semaphore an in-flight command still owns.
            _commandGates.Clear();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Raised on the UI dispatcher with a complete immutable projection.</summary>
    internal event Action<IReadOnlyList<DeviceCapabilityView>>? Changed;

    /// <summary>
    ///     Raised on the publishing thread after a descriptor set was accepted, with its cycle and
    ///     descriptor generations.
    /// </summary>
    internal event Action<long, long>? DescriptorsAccepted;

    internal void Attach(ICapabilityPublisher client, long cycleGeneration)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DetachUnderGate();
            _client = client;
            _cycleGeneration = cycleGeneration;
            _descriptorGeneration = 0;
            _descriptors.Clear();
            _orderedDescriptors = [];
            _states.Clear();
            _lastResults.Clear();
            _lastCommandValues.Clear();
            _pendingValues.Clear();
            _availability.Clear();
            _commandGates.Clear();
            _sections = [];
            _connected = true;
            client.DescriptorSetReceived += OnDescriptorSet;
            client.CapabilityStateReceived += OnStateDelta;
        }

        Publish();
    }

    /// <summary>Whether the publisher has reported a state for a capability of the accepted descriptor set.</summary>
    /// <param name="descriptor">The capability.</param>
    /// <returns>True once a state for it was accepted in the current descriptor generation.</returns>
    internal bool HasState(CapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_gate)
        {
            return _states.ContainsKey(Key(descriptor));
        }
    }

    /// <summary>Whether every capability of one descriptor set has reported a state.</summary>
    /// <param name="descriptorGeneration">The descriptor set asked about.</param>
    /// <returns>False while that set is not the accepted one or any of its capabilities has no state yet.</returns>
    internal bool HasStateForEveryDescriptor(long descriptorGeneration)
    {
        lock (_gate)
        {
            if (!_connected || _descriptorGeneration != descriptorGeneration)
            {
                return false;
            }

            foreach (var (key, _) in _orderedDescriptors)
            {
                if (!_states.ContainsKey(key))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Replaces the profile values desired state resolves from.</summary>
    /// <param name="deviceIdentityKey">The device the values were stored for, or null before it is known.</param>
    /// <param name="layers">Global and the running game's layer.</param>
    /// <param name="onAcPower">Current power source, for descriptors that differ by source.</param>
    internal void UpdateDesiredContext(string? deviceIdentityKey, ProfileLayers layers, bool onAcPower)
    {
        var global = Index(deviceIdentityKey, layers.Global);
        var game = Index(deviceIdentityKey, layers.Game);
        lock (_gate)
        {
            _globalDesired = global;
            _gameDesired = game;
            _onAcPower = onAcPower;
        }

        Publish();
    }

    internal async Task<CapabilityCommandResult> ExecuteAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        TimeSpan timeout,
        long? expectedCycle = null, long? expectedDescriptors = null, bool applyPowerPair = false,
        CancellationToken cancellationToken = default)
    {
        DeviceCapabilityKey key = new(capabilityId, instanceId);
        SemaphoreSlim commandGate;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_commandGates.TryGetValue(key, out commandGate!))
            {
                commandGate = new SemaphoreSlim(1, 1);
                _commandGates.Add(key, commandGate);
            }
        }

        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refusal = PrepareCommand(key, value, timeout, out var command, out var client, expectedCycle,
                expectedDescriptors, applyPowerPair);
            if (refusal is not null)
            {
                ReconcileResult(key, refusal);
                return refusal;
            }

            Publish();
            CapabilityCommandResult result;
            var terminal = true;
            try
            {
                var dispatch = await client.ExecuteCommandAsync(
                    command,
                    cancellationToken).ConfigureAwait(false);
                result = dispatch.Immediate;
                if (result.CommandId != command.CommandId)
                {
                    result = Uncertain(command, "The plugin returned a different command ID.");
                }
                else if (dispatch.LateCompletion is not null)
                {
                    terminal = false;
                    _ = ObserveLateCommandAsync(
                        key,
                        command.CommandId,
                        command.ExpectedCycleGeneration,
                        client,
                        dispatch.LateCompletion);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = Uncertain(command, ex.Message);
            }

            ReconcileResult(key, result, terminal);
            return result;
        }
        finally
        {
            commandGate.Release();
        }
    }

    internal IReadOnlyList<DeviceCapabilityView> Snapshot()
    {
        lock (_gate)
        {
            return BuildSnapshotUnderGate(DateTimeOffset.UtcNow);
        }
    }

    internal void MarkCycleGenerationChanged(long cycleGeneration)
    {
        lock (_gate)
        {
            AdvanceCycleUnderGate(cycleGeneration);
        }

        Publish();
    }

    private void AdvanceCycleUnderGate(long cycleGeneration)
    {
        if (cycleGeneration == _cycleGeneration)
        {
            return;
        }

        _cycleGeneration = cycleGeneration;
        _descriptorGeneration = 0;
        _states.Clear();
        _pendingValues.Clear();
        _lastResults.Clear();
        _lastCommandValues.Clear();
        _availability.Clear();
    }

    internal void CloseCommandAdmission()
    {
        lock (_gate)
        {
            _connected = false;
        }

        Publish();
    }

    internal void Detach()
    {
        lock (_gate)
        {
            DetachUnderGate();
            _pendingValues.Clear();
            _sections = [];
        }

        Publish();
    }

    private CapabilityCommandResult? PrepareCommand(
        DeviceCapabilityKey key,
        CapabilityValue? value,
        TimeSpan timeout,
        out CapabilityCommand command,
        out ICapabilityPublisher client,
        long? expectedCycle = null, long? expectedDescriptors = null, bool applyPowerPair = false)
    {
        var now = DateTimeOffset.UtcNow;
        var commandId = Guid.NewGuid();
        lock (_gate)
        {
            command = new CapabilityCommand
            {
                CommandId = commandId,
                CapabilityId = key.CapabilityId,
                InstanceId = key.InstanceId,
                RequestedValue = value,
                ExpectedDescriptorGeneration = _descriptorGeneration,
                ExpectedCycleGeneration = _cycleGeneration,
                Deadline = Deadline.After(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(5))
            };

            if (!_connected || _client is null)
            {
                client = null!;
                return Reject(command, CapabilityReasonCode.HostUnavailable,
                    _publisher is null
                        ? "The device plugin runtime is not connected."
                        : "The graphics plugin is not connected.", true);
            }

            client = _client;
            if ((expectedCycle is not null && expectedCycle != _cycleGeneration)
                || (expectedDescriptors is not null && expectedDescriptors != _descriptorGeneration))
            {
                return Reject(command, CapabilityReasonCode.HostUnavailable,
                    "The power preset belongs to an earlier device or descriptor generation.");
            }

            if (!_descriptors.TryGetValue(key, out var descriptor))
            {
                return Reject(command, CapabilityReasonCode.Unsupported,
                    "The capability is not present in the current descriptor set.");
            }

            if (applyPowerPair && descriptor.PairedPowerLimitId is null)
            {
                return Reject(command, CapabilityReasonCode.Unsupported,
                    "This capability does not declare a power pair.");
            }

            if (value?.IntegerValue is { } watts && key.InstanceId is null
                                                 && DevicePowerPair.Peer([.. _descriptors.Values], key.CapabilityId)
                                                     is { } peerDescriptor)
            {
                if (!_states.TryGetValue(Key(peerDescriptor), out var peer)
                    || !CanCommand(EvaluateFreshness(peer.State, FreshnessFor(peerDescriptor.Role), now,
                        _cycleGeneration)))
                {
                    return Reject(command, CapabilityReasonCode.ObservationExpired,
                        "The paired power limit is not available.");
                }

                command = command with
                {
                    PairedPowerLimitWatts = PairedWatts(descriptor, peerDescriptor, watts,
                        peer.State.ObservedValue?.IntegerValue, applyPowerPair)
                };
            }

            if (!_states.TryGetValue(key, out var rawState))
            {
                return Reject(command, CapabilityReasonCode.ObservationExpired,
                    "No current capability state has been observed.", true);
            }

            var state = EvaluateFreshness(
                rawState.State,
                FreshnessFor(descriptor.Role),
                now,
                _cycleGeneration);
            if (!CanCommand(state))
            {
                return Reject(
                    command,
                    state.Reason?.Code ?? CapabilityReasonCode.ObservationExpired,
                    state.Reason?.Detail ?? "Capability state is not current.",
                    state.Reason?.Retryable ?? true);
            }

            CapabilityReason? refusal = null;
            if (_onAcPower ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc)
            {
                refusal = new CapabilityReason(
                    CapabilityReasonCode.UnavailableOnPowerSource,
                    _onAcPower
                        ? "Capability is not available on AC power."
                        : "Capability is not available on battery.");
            }
            else if (value is null && !descriptor.SupportsAction)
            {
                refusal = new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    "Capability does not support being invoked as an action.");
            }
            else if (value is not null && !descriptor.SupportsWrite)
            {
                refusal = new CapabilityReason(CapabilityReasonCode.Unsupported, "Capability is read-only.");
            }
            else if (value is not null
                     && !DeviceCapabilityValidation.ValueMatches(value, descriptor, out var error))
            {
                refusal = new CapabilityReason(
                    CapabilityReasonCode.ValueOutOfRange,
                    error ?? "Capability value violates its descriptor.");
            }

            if (refusal is not null)
            {
                return Reject(
                    command,
                    refusal.Code,
                    refusal.Detail ?? "Command preflight failed.",
                    refusal.Retryable);
            }

            if (value is not null)
            {
                _pendingValues[key] = value;
                _lastCommandValues[key] = value;
            }
            else
            {
                _lastCommandValues.Remove(key);
            }

            return null;
        }
    }

    private void OnDescriptorSet(CapabilityDescriptorSet descriptors)
    {
        long acceptedCycle;
        lock (_gate)
        {
            // Resume publishes inside the lifecycle call, before the coordinator can synchronize.
            // Only the attached runtime may advance the cycle; a plugin-supplied number cannot.
            if (_client is { } client && client.CycleGeneration > _cycleGeneration
                                      && descriptors.CycleGeneration == client.CycleGeneration)
            {
                AdvanceCycleUnderGate(client.CycleGeneration);
            }

            if (!DeviceCapabilityValidation.TryValidateDescriptorSet(
                    descriptors,
                    _cycleGeneration,
                    _descriptorGeneration,
                    out var error))
            {
                Log.Warn($"{_label} descriptor set rejected: {error}");
                return;
            }

            // The manifest's capability list is what setup installed components for, so a role it
            // does not declare is a package defect, not a capability to show.
            if (_client is { } declaring
                && descriptors.Descriptors.FirstOrDefault(descriptor =>
                    !declaring.DeclaredCapabilities.Contains(descriptor.Role)) is { } undeclared)
            {
                Log.Warn($"{_label} descriptor set rejected: capability {undeclared.CapabilityId} uses role "
                         + $"{undeclared.Role}, which the package manifest does not declare.");
                return;
            }

            _descriptorGeneration = descriptors.Generation;
            _sections = DeviceSections.IncludePredefined(descriptors.Sections);
            _descriptors.Clear();
            foreach (var descriptor in descriptors.Descriptors)
            {
                _descriptors.Add(Key(descriptor), descriptor);
            }

            _orderedDescriptors = [.. _descriptors];
            Array.Sort(_orderedDescriptors, static (left, right) =>
            {
                var order = string.CompareOrdinal(left.Key.CapabilityId, right.Key.CapabilityId);
                return order != 0 ? order : string.CompareOrdinal(left.Key.InstanceId, right.Key.InstanceId);
            });

            _states.Clear();
            _pendingValues.Clear();
            _lastResults.Clear();
            _lastCommandValues.Clear();
            _availability.Clear();
            acceptedCycle = _cycleGeneration;
        }

        Publish();
        DescriptorsAccepted?.Invoke(acceptedCycle, descriptors.Generation);
    }

    private void OnStateDelta(CapabilityStateDelta delta)
    {
        lock (_gate)
        {
            var key = Key(delta.State);
            string? error = null;
            if (delta.Sequence <= 0
                || !_descriptors.TryGetValue(key, out var descriptor)
                || !DeviceCapabilityValidation.TryValidateState(
                    delta.State,
                    descriptor,
                    _descriptorGeneration,
                    _cycleGeneration,
                    out error))
            {
                Log.Change(
                    $"{ChangeKey("capability-state-rejected")}/{key}",
                    $"{_label} capability state rejected: key={key}, "
                    + $"{error ?? "invalid sequence or key"}");
                return;
            }

            if (_states.TryGetValue(key, out var existing)
                && delta.Sequence <= existing.Sequence)
            {
                Log.Change(
                    $"{ChangeKey("capability-delta-rejected")}/{key}",
                    $"{_label} capability delta rejected: key={key}, reason=OutOfOrder.");
                return;
            }

            _states[key] = delta;
            LogAvailabilityChange(key, delta.State);
        }

        Publish();
    }

    /// <summary>Logs a capability becoming available or unavailable, with the plugin's own reason.</summary>
    /// <param name="key">The capability that changed.</param>
    /// <param name="state">The state just applied.</param>
    /// <remarks>
    ///     The plugin already says exactly why a capability is unavailable — a gated firmware revision,
    ///     a missing prerequisite, a topology it could not match — and WSGM was throwing every one of
    ///     those away. A device reporting itself "partly available" with no record of which parts or
    ///     why cannot be diagnosed from a pasted log, which is the only way most of these devices are
    ///     reachable. Logged on change so a capability that is simply unavailable does not repeat.
    ///     <para>
    ///         Called under <c>_gate</c>, after the delta is accepted, so what is logged is what was
    ///         actually applied rather than what arrived.
    ///     </para>
    /// </remarks>
    private void LogAvailabilityChange(DeviceCapabilityKey key, CapabilityState state)
    {
        var previous = _availability.TryGetValue(key, out var known) && known;
        var first = !_availability.ContainsKey(key);
        _availability[key] = state.Available;
        if (!first && previous == state.Available)
        {
            return;
        }

        if (state.Available)
        {
            Log.Info($"{_label} capability available: {key}.");
            return;
        }

        var reason = state.Reason?.Detail is { Length: > 0 } detail
            ? $"{state.Reason.Code}: {detail}"
            : state.Reason?.Code.ToString() ?? "no reason given";
        Log.Warn($"{_label} capability unavailable: {key} — {reason}");
    }

    /// <summary>A change key namespaced by publisher, so two publishers never share one.</summary>
    private string ChangeKey(string name)
    {
        return _publisher is null ? $"device-{name}" : $"{_publisher}/{name}";
    }

    private async Task ObserveLateCommandAsync(
        DeviceCapabilityKey key,
        Guid commandId,
        long cycleGeneration,
        ICapabilityPublisher client,
        Task<CapabilityCommandResult> completion)
    {
        var result = await completion.ConfigureAwait(false);
        lock (_gate)
        {
            if (!_connected
                || !ReferenceEquals(_client, client)
                || _cycleGeneration != cycleGeneration
                || result.CommandId != commandId)
            {
                Log.Warn(
                    $"Late {_label} command result ignored: command={result.CommandId}, expected={commandId}, "
                    + $"resultGeneration={cycleGeneration}, activeGeneration={_cycleGeneration}, "
                    + $"connected={_connected}, sameRuntime={ReferenceEquals(_client, client)}.");
                return;
            }
        }

        Log.Info($"Late {_label} command result reconciled: command={result.CommandId}, "
                 + $"capability={key}, outcome={result.Outcome}.");
        ReconcileResult(key, result);
    }

    private void ReconcileResult(
        DeviceCapabilityKey key,
        CapabilityCommandResult result,
        bool terminal = true)
    {
        lock (_gate)
        {
            if (terminal)
            {
                _pendingValues.Remove(key);
            }

            _lastResults[key] = result;
        }

        // Keyed on the capability so a run of identical outcomes collapses: the interesting event
        // is an outcome changing, not the hundreds of times a limit reapplied exactly as before.
        // The command id is deliberately out of the deduplicated text — it is unique per call, so
        // including it would defeat the key and it correlates nothing anyone reads later.
        Log.Change(
            $"{ChangeKey("command")}/{key}",
            $"{_label} command: capability={key}, outcome={result.Outcome}, "
            + $"rollback={result.Rollback}.",
            result.Outcome is CommandOutcome.AppliedVerified ? LogLevel.Info : LogLevel.Warn);
        Publish();
    }

    private List<DeviceCapabilityView> BuildSnapshotUnderGate(DateTimeOffset now)
    {
        List<DeviceCapabilityView> views = [];
        foreach (var (key, descriptor) in _orderedDescriptors)
        {
            var state = _states.TryGetValue(key, out var latest)
                ? latest.State
                : UnknownState(key);
            if (!_connected)
            {
                state = state with
                {
                    Available = false,
                    Quality = HardwareStateQuality.Stale,
                    Reason = new CapabilityReason(
                        CapabilityReasonCode.HostUnavailable,
                        _publisher is null
                            ? "The device plugin is disconnected."
                            : "The graphics plugin is disconnected.",
                        true)
                };
            }
            else
            {
                state = EvaluateFreshness(
                    state,
                    FreshnessFor(descriptor.Role),
                    now,
                    _cycleGeneration);
            }

            var desired = ResolveDesired(key, descriptor.ProfileScope);
            var global = _globalDesired.GetValueOrDefault(key);
            var outOfRange = (desired.Value is not null
                              && !DeviceCapabilityValidation.ValueMatches(desired.Value, descriptor, out _))
                             || (global is not null
                                 && descriptor.ProfileScope is not CapabilityProfileScope.Switched
                                 && !DeviceCapabilityValidation.ValueMatches(global, descriptor, out _));
            _pendingValues.TryGetValue(key, out var pending);
            _lastResults.TryGetValue(key, out var result);
            _lastCommandValues.TryGetValue(key, out var commanded);
            views.Add(new DeviceCapabilityView(
                descriptor,
                new CapabilityProjection
                {
                    State = state,
                    DesiredValue = desired.Value,
                    DesiredSource = desired.Source,
                    GlobalDesiredValue = global,
                    ProfileScope = descriptor.ProfileScope,
                    ApplyTiming = descriptor.ApplyTiming,
                    PendingValue = pending,
                    Progress = Progress(pending, result),
                    DesiredValueOutOfRange = outOfRange
                },
                result,
                commanded) { Publisher = _publisher });
        }

        return views;
    }

    /// <summary>The value a capability shows as wanted, by its profile scope.</summary>
    /// <remarks>
    ///     A switched or native per-application value resolves the game over Global. The native one is
    ///     shown that way because its driver applies the game's value itself; WSGM still commands only the
    ///     Global value, read from <see cref="CapabilityProjection.GlobalDesiredValue" />. A global-only
    ///     value never reads the game layer, so it is never marked as the game's.
    /// </remarks>
    private Resolved<CapabilityValue?> ResolveDesired(DeviceCapabilityKey key, CapabilityProfileScope scope)
    {
        if (scope is not CapabilityProfileScope.GlobalOnly && _gameDesired.TryGetValue(key, out var game))
        {
            return new Resolved<CapabilityValue?>(game, ProfileSource.Game);
        }

        return _globalDesired.TryGetValue(key, out var global)
            ? new Resolved<CapabilityValue?>(global, ProfileSource.Global)
            : new Resolved<CapabilityValue?>(null, ProfileSource.None);
    }

    private static Dictionary<DeviceCapabilityKey, CapabilityValue> Index(
        string? deviceIdentityKey,
        ProfileValues? values)
    {
        Dictionary<DeviceCapabilityKey, CapabilityValue> index = [];
        if (deviceIdentityKey is null)
        {
            return index;
        }

        foreach (var entry in values?.Device ?? [])
        {
            if (entry.Value is not null
                && string.Equals(entry.DeviceIdentityKey, deviceIdentityKey, StringComparison.Ordinal))
            {
                index[new DeviceCapabilityKey(entry.CapabilityId, entry.InstanceId)] = entry.Value;
            }
        }

        return index;
    }

    private CapabilityState UnknownState(DeviceCapabilityKey key)
    {
        return new CapabilityState
        {
            CapabilityId = key.CapabilityId,
            InstanceId = key.InstanceId,
            Available = false,
            Quality = HardwareStateQuality.Unknown,
            DescriptorGeneration = _descriptorGeneration,
            CycleGeneration = _cycleGeneration,
            Reason = new CapabilityReason(
                CapabilityReasonCode.ObservationExpired,
                "No state has been published for this descriptor.",
                true)
        };
    }

    private void DetachUnderGate()
    {
        if (_client is not null)
        {
            _client.DescriptorSetReceived -= OnDescriptorSet;
            _client.CapabilityStateReceived -= OnStateDelta;
        }

        _client = null;
        _connected = false;
        _availability.Clear();
        _commandGates.Clear();
    }

    private void Publish()
    {
        // One posted build per burst of changes. It builds when the post runs, so it carries
        // everything that arrived in between instead of a snapshot per change that only the last
        // of was ever delivered.
        lock (_gate)
        {
            if (_publishPending)
            {
                return;
            }

            _publishPending = true;
        }

        _postToUi(_publishPosted);
    }

    private void PublishPosted()
    {
        IReadOnlyList<DeviceCapabilityView> snapshot;
        lock (_gate)
        {
            _publishPending = false;
            if (_disposed)
            {
                return;
            }

            snapshot = BuildSnapshotUnderGate(DateTimeOffset.UtcNow);
        }

        Changed?.Invoke(snapshot);
    }

    private static CapabilityCommandResult Reject(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        bool retryable = false)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(code, detail, retryable),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static CapabilityCommandResult Uncertain(CapabilityCommand command, string detail)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable, detail, true),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static CommandProgress Progress(
        CapabilityValue? pending,
        CapabilityCommandResult? result)
    {
        if (pending is not null)
        {
            return CommandProgress.Pending;
        }

        return result?.Outcome switch
        {
            CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified =>
                CommandProgress.Completed,
            CommandOutcome.TimedOut or CommandOutcome.Indeterminate => CommandProgress.Uncertain,
            CommandOutcome.Rejected => CommandProgress.Failed,
            _ => CommandProgress.Idle
        };
    }

    /// <summary>How long a live reading stays current, or null for a value that never expires.</summary>
    /// <remarks>
    ///     Only a live measurement such as a fan RPM or a temperature ages out. A setting is whatever was
    ///     last written or read, however long ago, as HC treats it: expiring it made every write and
    ///     control wait for a fresh readback, which a device like the Ally never delivers.
    /// </remarks>
    private static TimeSpan? FreshnessFor(CapabilityRole role)
    {
        return role is CapabilityRole.Telemetry or CapabilityRole.FanMeasuredRpm ? TimeSpan.FromSeconds(5) : null;
    }

    /// <summary>
    ///     Returns the state as it should be presented now, downgrading it to
    ///     <see cref="HardwareStateQuality.Stale" /> when it can no longer be trusted.
    /// </summary>
    private static CapabilityState EvaluateFreshness(
        CapabilityState state,
        TimeSpan? maxAge,
        DateTimeOffset now,
        long currentCycleGeneration)
    {
        // A faulted capability is already saying something stronger than "old". Downgrading it to
        // stale would lose the fault.
        if (state.Quality is HardwareStateQuality.Faulted or HardwareStateQuality.Unknown)
        {
            return state;
        }

        // A generation change invalidates the observation outright, regardless of age: the handles
        // and the hardware state it described belong to a device that no longer exists.
        if (state.CycleGeneration != currentCycleGeneration)
        {
            return Stale(state, CapabilityReasonCode.GenerationChanged,
                "Observed under a previous process/reconnect cycle.");
        }

        if (maxAge is { } limit && (state.ObservedAt is not { } observedAt || now - observedAt > limit))
        {
            return Stale(state, CapabilityReasonCode.ObservationExpired,
                $"Observation is older than {maxAge}.");
        }

        return state;
    }

    /// <summary>The wattage the other limit of a declared power pair takes when one of them is written.</summary>
    /// <remarks>
    ///     Pair policy is the host's, where Handheld Companion keeps it too (its performance page couples
    ///     PL1 and PL2; no device class does): the sustained limit never asks to exceed the boost limit.
    ///     Raising the sustained limit past the boost limit carries the boost limit up with it, a boost
    ///     ceiling below the sustained limit carries that limit down ("cap this app here"), and a unified
    ///     target moves both to it. A paired limit the plugin cannot observe yet counts as the commanded
    ///     wattage. The result stays inside the paired descriptor's range; a step it misses is refused by
    ///     the plugin, never rounded.
    /// </remarks>
    /// <param name="commanded">The limit being written.</param>
    /// <param name="paired">The other limit of its pair.</param>
    /// <param name="watts">The commanded wattage.</param>
    /// <param name="pairedWatts">The other limit's observed wattage, or null when it is unknown.</param>
    /// <param name="unified">Whether both limits move to <paramref name="watts" />.</param>
    /// <returns>The wattage the command carries for the other limit.</returns>
    internal static int PairedWatts(
        CapabilityDescriptor commanded,
        CapabilityDescriptor paired,
        int watts,
        int? pairedWatts,
        bool unified)
    {
        var current = pairedWatts ?? watts;
        var target = unified
            ? watts
            : commanded.Role == CapabilityRole.PowerSustainedLimit
                ? Math.Max(current, watts)
                : Math.Min(current, watts);
        return paired is { Minimum: { } minimum, Maximum: { } maximum }
            ? Math.Clamp(target, minimum, maximum)
            : target;
    }

    /// <summary>Whether a command may be issued against this state.</summary>
    /// <remarks>
    ///     Readback is not a precondition: many firmwares cannot report what they were set to, and a
    ///     capability that was never read (<see cref="HardwareStateQuality.Unknown" />) is still
    ///     writable while the plugin reports it available. An expired or faulted observation is not.
    /// </remarks>
    internal static bool CanCommand(CapabilityState state)
    {
        return state is
        {
            Available: true,
            Quality: not (HardwareStateQuality.Stale or HardwareStateQuality.Faulted)
        };
    }

    private static CapabilityState Stale(
        CapabilityState state,
        CapabilityReasonCode code,
        string detail)
    {
        return state with
        {
            Quality = HardwareStateQuality.Stale,
            Available = false,
            Reason = new CapabilityReason(code, detail, true)
        };
    }

    private static DeviceCapabilityKey Key(CapabilityDescriptor descriptor)
    {
        return new DeviceCapabilityKey(descriptor.CapabilityId, descriptor.InstanceId);
    }

    private static DeviceCapabilityKey Key(CapabilityState state)
    {
        return new DeviceCapabilityKey(state.CapabilityId, state.InstanceId);
    }
}

/// <summary>Structural and semantic validation applied before plugin data enters WSGM state.</summary>
internal static class DeviceCapabilityValidation
{
    internal static bool TryValidateDescriptorSet(
        CapabilityDescriptorSet set,
        long cycleGeneration,
        long previousGeneration,
        out string? error)
    {
        if (set.Generation <= previousGeneration || set.CycleGeneration != cycleGeneration)
        {
            error = "Descriptor or device generation is stale.";
            return false;
        }

        var sections = DeviceSections.All.ToDictionary(section => section.SectionId, StringComparer.Ordinal);
        HashSet<string> declaredIds = new(StringComparer.Ordinal);
        foreach (var section in set.Sections)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (section is null)
            {
                error = "Descriptor set contains a null section.";
                return false;
            }

            if (!section.TryValidate(out error))
            {
                return false;
            }

            if (!declaredIds.Add(section.SectionId))
            {
                error = $"Descriptor set declares section '{section.SectionId}' more than once.";
                return false;
            }

            sections[section.SectionId] = section;
        }

        HashSet<DeviceCapabilityKey> keys = [];
        foreach (var descriptor in set.Descriptors)
        {
            if (TryValidateDescriptor(descriptor, out error)
                && TryValidatePlacement(descriptor, sections, out error)
                && keys.Add(new DeviceCapabilityKey(
                    descriptor.CapabilityId,
                    descriptor.InstanceId)))
            {
                continue;
            }

            error ??= "Descriptor keys are duplicated.";
            return false;
        }

        return CapabilityLayout.TryValidate(set.Descriptors, out error)
               && DevicePowerPreset.TryValidate(set.Descriptors, out error)
               && DevicePowerPair.TryValidate(set.Descriptors, out error);
    }

    internal static bool TryValidateState(
        CapabilityState state,
        CapabilityDescriptor descriptor,
        long descriptorGeneration,
        long cycleGeneration,
        out string? error)
    {
        if (state.DescriptorGeneration != descriptorGeneration
            || state.CycleGeneration != cycleGeneration)
        {
            error = "State generation does not match the current descriptor and cycle.";
            return false;
        }

        if (state.ObservedValue is not null
            && !ValueMatches(state.ObservedValue, descriptor, out error))
        {
            return false;
        }

        if (state.Quality is HardwareStateQuality.Verified && state.ObservedValue is null)
        {
            error = "Verified state must carry a readback value.";
            return false;
        }

        error = null;
        return true;
    }

    internal static bool ValueMatches(
        CapabilityValue value,
        CapabilityDescriptor descriptor,
        out string? error)
    {
        if (value.Kind != descriptor.ValueKind)
        {
            error = "Capability value kind differs from its descriptor.";
            return false;
        }

        var valid = value.Kind switch
        {
            CapabilityValueKind.Boolean => value.BooleanValue is not null,
            CapabilityValueKind.Integer => value.IntegerValue is { } integer
                                           && (descriptor.Minimum is null || integer >= descriptor.Minimum)
                                           && (descriptor.Maximum is null || integer <= descriptor.Maximum)
                                           && (descriptor.Step is null or <= 0
                                               || (integer - (descriptor.Minimum ?? 0)) % descriptor.Step == 0),
            CapabilityValueKind.Choice => value.ChoiceValue is { Length: > 0 } choice
                                          && descriptor.Choices.Any(item => string.Equals(
                                              item.Value,
                                              choice,
                                              StringComparison.Ordinal)),
            CapabilityValueKind.Color => value.ColorValue is >= 0 and <= 0xFFFFFF,
            CapabilityValueKind.Curve => CurveIsValid(value.CurveValue, descriptor),
            CapabilityValueKind.Text => PlainText.TryValidate(
                value.TextValue,
                descriptor.MaximumLength ?? 0,
                "text",
                out _),
            _ => false
        };
        error = valid ? null : "Capability value violates its descriptor shape or bounds.";
        return valid;
    }

    /// <summary>Checks a descriptor's section and category references against the declared layout.</summary>
    /// <remarks>
    ///     A section declared in the set is the plugin authoring its own overlay surface, so any role
    ///     may be placed there. Outside that layout the old rule stands: a semantic role keeps the home
    ///     WSGM gives it, and only a generic role may name a settings-manifest section — an unknown id
    ///     there falls back to a WSGM-owned group instead of failing, which is why it is not an error.
    /// </remarks>
    private static bool TryValidatePlacement(
        CapabilityDescriptor descriptor,
        Dictionary<string, CapabilitySection> sections,
        out string? error)
    {
        CapabilitySection? home = null;
        if (descriptor.SectionId is { } sectionId
            && !sections.TryGetValue(sectionId, out home))
        {
            if (!descriptor.Role.IsGeneric())
            {
                // Named in the error, because from the plugin author's side this looks like a
                // section that was simply ignored.
                error =
                    $"Capability role {descriptor.Role} may not declare the undeclared section "
                    + $"'{sectionId}': a semantic role keeps the placement WSGM gives it on every "
                    + "device unless the descriptor set declares the layout.";
                return false;
            }

            home = null;
        }

        if (descriptor.CategoryId is { } categoryId
            && (home is null
                || !home.Categories.Any(category => string.Equals(
                    category.CategoryId,
                    categoryId,
                    StringComparison.Ordinal))))
        {
            error =
                $"Capability '{descriptor.CapabilityId}' names category '{categoryId}' that its "
                + "declared section does not carry.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateDescriptor(CapabilityDescriptor descriptor, out string? error)
    {
        if (!PlainText.IsIdentifier(descriptor.CapabilityId)
            || (descriptor.InstanceId is not null && !PlainText.IsIdentifier(descriptor.InstanceId)))
        {
            error = "Capability or instance ID is invalid.";
            return false;
        }

        if (!descriptor.Display.TryValidate(out error))
        {
            return false;
        }

        if (descriptor.SectionId is { } sectionId
            && !PlainText.IsIdentifier(sectionId))
        {
            error = "Capability section ID is invalid.";
            return false;
        }

        if (descriptor.CategoryId is { } categoryId
            && !PlainText.IsIdentifier(categoryId))
        {
            error = "Capability category ID is invalid.";
            return false;
        }

        if (descriptor is { SupportsRead: false, SupportsWrite: false, SupportsAction: false })
        {
            error = "Descriptor exposes no readable, writable, or actionable operation.";
            return false;
        }

        if (descriptor.ValueKind is CapabilityValueKind.None != descriptor.SupportsAction
            || (descriptor.ValueKind is CapabilityValueKind.None
                && (descriptor.SupportsRead || descriptor.SupportsWrite)))
        {
            error = "Action and value-bearing descriptor shapes are inconsistent.";
            return false;
        }

        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (descriptor.ValueKind)
        {
            case CapabilityValueKind.Integer
                when descriptor.Minimum is null
                     || descriptor.Maximum is null
                     || descriptor.Minimum > descriptor.Maximum
                     || descriptor.Step is null or <= 0:
                error = "Integer descriptors require an ordered range and positive step.";
                return false;
            case CapabilityValueKind.Choice
                when descriptor.Choices.Count is 0
                     || descriptor.Choices.Any(choice => !PlainText.IsIdentifier(choice.Value))
                     || descriptor.Choices.Select(choice => choice.Value).Distinct(StringComparer.Ordinal)
                         .Count() != descriptor.Choices.Count:
                error = "Choice descriptor values are empty, invalid, or duplicated.";
                return false;
        }

        if (descriptor.ValueKind is not CapabilityValueKind.Choice && descriptor.Choices.Count != 0)
        {
            error = "Only choice descriptors may carry choices.";
            return false;
        }

        // Text is the one value shape with no natural bound, so the descriptor must supply one.
        // Without this a plugin could publish a text capability whose value is unbounded, which is
        // exactly the case PlainText exists to prevent.
        if (descriptor.ValueKind is CapabilityValueKind.Text
            && descriptor.MaximumLength is not > 0)
        {
            error = "Text descriptors require a positive maximumLength.";
            return false;
        }

        if (descriptor.ValueKind is not CapabilityValueKind.Text && descriptor.MaximumLength is not null)
        {
            error = "Only text descriptors may carry a maximumLength.";
            return false;
        }

        if (!RoleMatchesValueKind(descriptor.Role, descriptor.ValueKind))
        {
            error = "Capability role and value kind are inconsistent.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool RoleMatchesValueKind(CapabilityRole role, CapabilityValueKind kind)
    {
        return role switch
        {
            CapabilityRole.FanCurve => kind is CapabilityValueKind.Curve,
            CapabilityRole.GenericAction => kind is CapabilityValueKind.None,
            CapabilityRole.GenericToggle
                or CapabilityRole.LightingPower
                or CapabilityRole.VariableRefreshRate
                or CapabilityRole.ChargeBypass => kind is CapabilityValueKind.Boolean,
            CapabilityRole.GenericChoice
                or CapabilityRole.ScenarioMode
                or CapabilityRole.FanMode
                or CapabilityRole.ChargeProtectionMode
                or CapabilityRole.LightingEffect
                or CapabilityRole.ControllerSource
                or CapabilityRole.MotionSource => kind is CapabilityValueKind.Choice,
            CapabilityRole.LightingZoneColor => kind is CapabilityValueKind.Color,
            CapabilityRole.PowerSustainedLimit
                or CapabilityRole.PowerSlowLimit
                or CapabilityRole.PowerFastLimit
                or CapabilityRole.PowerPeakLimit
                or CapabilityRole.FanDuty
                or CapabilityRole.FanTargetRpm
                or CapabilityRole.FanMeasuredRpm
                or CapabilityRole.ChargeLimit
                or CapabilityRole.LightingBrightness
                or CapabilityRole.LightingEffectSpeed
                or CapabilityRole.GenericRange => kind is CapabilityValueKind.Integer,
            CapabilityRole.OemControl or CapabilityRole.HapticSink => kind is CapabilityValueKind.None,
            CapabilityRole.GenericText => kind is CapabilityValueKind.Text,
            CapabilityRole.Telemetry or CapabilityRole.GenericReadOnly =>
                kind is CapabilityValueKind.Boolean
                    or CapabilityValueKind.Integer
                    or CapabilityValueKind.Choice
                    // A read-only string — a firmware revision, a mode name the device reports.
                    or CapabilityValueKind.Text,
            _ => true
        };
    }

    /// <summary>
    ///     Point count, strictly ascending inputs, and outputs inside whatever bounds the
    ///     descriptor declared — the same three the authored-profile check applies.
    /// </summary>
    /// <remarks>
    ///     The output bounds are checked here and not only in <see cref="DeviceProfileValidation" />
    ///     because a curve can also be written straight through <c>ExecuteCapabilityAsync</c>, without
    ///     passing a profile. Every other numeric kind on this path is held to the declared minimum and
    ///     maximum, and the refusal message promises "shape or bounds" for all of them. Only the bounds
    ///     the device actually declared are enforced: a descriptor that leaves one unset is saying it
    ///     has no limit there, and inventing one would refuse a curve the device would have accepted.
    /// </remarks>
    private static bool CurveIsValid(IReadOnlyList<CurvePoint> points, CapabilityDescriptor descriptor)
    {
        if (points.Count is 0)
        {
            return false;
        }

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (index > 0 && point.Input <= points[index - 1].Input)
            {
                return false;
            }

            if ((descriptor.Minimum is { } minimum && point.Output < minimum)
                || (descriptor.Maximum is { } maximum && point.Output > maximum))
            {
                return false;
            }
        }

        return true;
    }
}
