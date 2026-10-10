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
/// <param name="CapabilityId">Semantic capability identifier, unique together with the instance identifier.</param>
/// <param name="InstanceId">Optional subdevice/zone identifier; null denotes the uninstanced capability.</param>
internal readonly record struct DeviceCapabilityKey(string CapabilityId, string? InstanceId)
{
    /// <summary>Formats the key for diagnostics, separating a nonempty instance with a hash.</summary>
    /// <returns>The capability identifier alone, or capability and instance identifiers joined by <c>#</c>.</returns>
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
///     Validates and projects the semantic capability stream owned by one active publisher.
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

    /// <summary>Observers of late command completions, joined by <see cref="DisposeAsync" />.</summary>
    private readonly HashSet<Task> _lateObservers = [];

    private readonly Dictionary<DeviceCapabilityKey, Guid> _latestCommands = [];

    /// <summary>Stops waiting for late completions once the router is disposed.</summary>
    /// <remarks>
    ///     Never disposed: an admitted command still in flight at disposal may read its token. It owns
    ///     no timer, so leaving it to the collector releases nothing late.
    /// </remarks>
    private readonly CancellationTokenSource _lifetime = new();

    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _pendingValues = [];

    private readonly Action<Action> _postToUi;

    private readonly Action _publishPosted;

    /// <summary>The publisher's profile key, or null for the device package.</summary>
    private readonly string? _publisher;

    /// <summary>Latest accepted state per capability.</summary>
    /// <remarks>
    ///     The high-rate state channel does not promise ordering, and a delayed older sample overwriting
    ///     a newer one is not cosmetic: it can restore a "fresh" reading the device has already moved
    ///     past, and the UI would then command against it. Sequence numbers order observations from
    ///     the currently attached publisher; detaching clears its observations.
    /// </remarks>
    private readonly Dictionary<DeviceCapabilityKey, CapabilityStateDelta> _states = [];

    private readonly Func<DateTimeOffset> _utcNow;

    private ICapabilityPublisher? _client;
    private bool _connected;
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

    /// <summary>Creates the projection owner for one device or graphics capability publisher.</summary>
    /// <param name="postToUi">Posts the projection build to the UI dispatcher.</param>
    /// <param name="publisher">A graphics publisher's profile key, or null for the device package.</param>
    /// <param name="utcNow">Observation clock, or the current UTC time.</param>
    internal DeviceCapabilityRouter(Action<Action> postToUi, string? publisher = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(postToUi);
        _postToUi = postToUi;
        _publisher = publisher;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
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

    /// <summary>Completion of the late-result observers currently owned by this router.</summary>
    internal Task LateCommandCompletion
    {
        get
        {
            lock (_gate)
            {
                return Task.WhenAll(_lateObservers);
            }
        }
    }

    /// <summary>Detaches the publisher and cancels and joins late-result observers.</summary>
    /// <returns>Observer cleanup completion; it does not wait for or dispose underlying plugin calls.</returns>
    /// <remarks>In-flight commands retain their own gates until they return; disposal cannot abort their native effects.</remarks>
    public async ValueTask DisposeAsync()
    {
        Task[] observers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DetachUnderGate();
            // An admitted ExecuteAsync releases its local gate in finally. Clearing the index
            // blocks reuse without disposing a semaphore an in-flight command still owns.
            _commandGates.Clear();
            observers = [.. _lateObservers];
            _lateObservers.Clear();
        }

        // A late result after detaching would be ignored anyway, so the observers stop waiting
        // for the plugin and the join below cannot outlast a hung command.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(observers).ConfigureAwait(false);
    }

    /// <summary>Raised on the UI dispatcher with a complete immutable projection.</summary>
    internal event Action<IReadOnlyList<DeviceCapabilityView>>? Changed;

    /// <summary>
    ///     Raised on the publishing thread after a complete descriptor set was accepted.
    /// </summary>
    internal event Action? DescriptorsAccepted;

    /// <summary>Replaces the borrowed publisher and clears all descriptors, observations, and command projections.</summary>
    /// <param name="client">Publisher whose events are subscribed until detach or disposal; ownership is not transferred.</param>
    /// <exception cref="ObjectDisposedException">The router has been disposed.</exception>
    internal void Attach(ICapabilityPublisher client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DetachUnderGate();
            _client = client;
            _descriptors.Clear();
            _orderedDescriptors = [];
            _states.Clear();
            _lastResults.Clear();
            _lastCommandValues.Clear();
            _pendingValues.Clear();
            _latestCommands.Clear();
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
    /// <returns>True once a state for it was accepted in the current descriptor set.</returns>
    internal bool HasState(CapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_gate)
        {
            return _states.ContainsKey(Key(descriptor));
        }
    }

    /// <summary>Whether every capability of one descriptor set has reported a state.</summary>
    /// <returns>False while that set is not the accepted one or any of its capabilities has no state yet.</returns>
    internal bool HasStateForEveryDescriptor()
    {
        lock (_gate)
        {
            if (!_connected || _client is not { IsActive: true })
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

    /// <summary>Serializes one capability request, validates its current descriptor/state, and dispatches once.</summary>
    /// <param name="capabilityId">Semantic identifier in the accepted descriptor set.</param>
    /// <param name="instanceId">Exact instance identifier, or null for an uninstanced capability.</param>
    /// <param name="value">Requested value, or null for an action-only capability.</param>
    /// <param name="timeout">Budget assigned after per-capability admission; nonpositive values use five seconds.</param>
    /// <param name="applyPowerPair">Whether to carry the host-computed paired power value with this request.</param>
    /// <param name="cancellationToken">
    ///     Cancels lane waiting or dispatch; cancellation after dispatch can mean uncertain
    ///     effects.
    /// </param>
    /// <returns>
    ///     Immediate rejection, completion, or uncertainty. Late completion updates the projection separately;
    ///     neither this method nor late observation automatically retries a write.
    /// </returns>
    internal async Task<CapabilityCommandResult> ExecuteAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        TimeSpan timeout,
        bool applyPowerPair = false,
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
            var refusal = PrepareCommand(key, value, timeout, out var command, out var client, applyPowerPair);
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
                    TrackLateObserver(ObserveLateCommandAsync(
                        key,
                        command.CommandId,
                        client,
                        dispatch.LateCompletion));
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

    /// <summary>Builds a complete projection using the current descriptors, desired layers, and observation age.</summary>
    /// <returns>A captured capability list; taking a snapshot does not dispatch or persist values.</returns>
    internal IReadOnlyList<DeviceCapabilityView> Snapshot()
    {
        lock (_gate)
        {
            return BuildSnapshotUnderGate(_utcNow());
        }
    }

    /// <summary>Marks this publisher disconnected so queued/new requests cannot dispatch.</summary>
    /// <remarks>Existing native work is not canceled or rolled back by this projection change.</remarks>
    internal void CloseCommandAdmission()
    {
        lock (_gate)
        {
            _connected = false;
        }

        Publish();
    }

    /// <summary>Unsubscribes the publisher and clears pending commands and section presentation.</summary>
    /// <remarks>The publisher is borrowed and is not stopped or disposed here.</remarks>
    internal void Detach()
    {
        lock (_gate)
        {
            DetachUnderGate();
            _pendingValues.Clear();
            _latestCommands.Clear();
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
        bool applyPowerPair = false)
    {
        var now = _utcNow();
        var commandId = Guid.NewGuid();
        lock (_gate)
        {
            command = new CapabilityCommand
            {
                CommandId = commandId,
                CapabilityId = key.CapabilityId,
                InstanceId = key.InstanceId,
                RequestedValue = value,
                Deadline = Deadline.After(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(5))
            };
            _latestCommands[key] = commandId;

            if (!_connected || _client is not { IsActive: true })
            {
                client = null!;
                return Reject(command, CapabilityReasonCode.HostUnavailable,
                    _publisher is null
                        ? "The device plugin runtime is not connected."
                        : "The graphics plugin is not connected.", true);
            }

            client = _client;
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
                    || !CanCommand(EvaluateFreshness(peer.State, FreshnessFor(peerDescriptor.Role), now)))
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
                now);
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
                     && !CapabilityValueValidation.ValueMatches(value, descriptor, out var error))
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
        string? error;
        bool accepted;
        lock (_gate)
        {
            accepted = AcceptDescriptorSetUnderGate(descriptors, out error);
        }

        if (!accepted)
        {
            Log.Warn($"{_label} descriptor set rejected: {error}");
            return;
        }

        Publish();
        DescriptorsAccepted?.Invoke();
    }

    private bool AcceptDescriptorSetUnderGate(CapabilityDescriptorSet descriptors,
        out string? error)
    {
        error = null;
        if (!DeviceCapabilityValidation.TryValidateDescriptorSet(
                descriptors,
                out error))
        {
            return false;
        }

        // The manifest's capability list is what setup installed components for, so a role it
        // does not declare is a package defect, not a capability to show.
        if (_client is { } declaring
            && descriptors.Descriptors.FirstOrDefault(descriptor =>
                !declaring.DeclaredCapabilities.Contains(descriptor.Role)) is { } undeclared)
        {
            error = $"capability {undeclared.CapabilityId} uses role "
                    + $"{undeclared.Role}, which the package manifest does not declare.";
            return false;
        }


        _sections = DeviceSectionLayout.IncludePredefined(descriptors.Sections);
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
        _latestCommands.Clear();
        _lastResults.Clear();
        _lastCommandValues.Clear();
        _availability.Clear();

        return true;
    }

    private void OnStateDelta(CapabilityStateDelta delta)
    {
        var key = Key(delta.State);
        string? rejected = null;
        var outOfOrder = false;
        var availabilityChanged = false;
        var stateChanged = false;
        lock (_gate)
        {
            string? error = null;
            if (delta.Sequence <= 0
                || !_descriptors.TryGetValue(key, out var descriptor)
                || !DeviceCapabilityValidation.TryValidateState(
                    delta.State,
                    descriptor,
                    out error))
            {
                rejected = error ?? "invalid sequence or key";
            }
            else if (_states.TryGetValue(key, out var existing)
                     && delta.Sequence <= existing.Sequence)
            {
                outOfOrder = true;
            }
            else
            {
                stateChanged = existing is null || !SameState(existing.State, delta.State);
                _states[key] = delta;
                availabilityChanged = !_availability.TryGetValue(key, out var previous)
                                      || previous != delta.State.Available;
                _availability[key] = delta.State.Available;
            }
        }

        if (rejected is not null)
        {
            Log.Change($"{ChangeKey("capability-state-rejected")}/{key}",
                $"{_label} capability state rejected: key={key}, {rejected}");
            return;
        }

        if (outOfOrder)
        {
            Log.Change($"{ChangeKey("capability-delta-rejected")}/{key}",
                $"{_label} capability delta rejected: key={key}, reason=OutOfOrder.");
            return;
        }

        if (availabilityChanged)
        {
            LogAvailabilityChange(key, delta.State);
        }

        if (stateChanged)
        {
            Publish();
        }
    }

    private static bool SameState(CapabilityState left, CapabilityState right)
    {
        return left.CapabilityId == right.CapabilityId && left.InstanceId == right.InstanceId
                                                       && left.Available == right.Available &&
                                                       left.Quality == right.Quality
                                                       && left.Reason == right.Reason &&
                                                       left.ObservedAt == right.ObservedAt
                                                       && (left.ObservedValue == right.ObservedValue
                                                           || (left.ObservedValue is { } previous &&
                                                               right.ObservedValue is { } current
                                                               && CapabilityValues.Same(previous, current)));
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
    ///         The change is captured under <c>_gate</c> after accepting the delta; logging runs outside it.
    ///     </para>
    /// </remarks>
    private void LogAvailabilityChange(DeviceCapabilityKey key, CapabilityState state)
    {
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

    /// <summary>Keeps a late-completion observer until disposal joins it.</summary>
    /// <param name="observer">The observer task.</param>
    private void TrackLateObserver(Task observer)
    {
        lock (_gate)
        {
            _lateObservers.RemoveWhere(static task => task.IsCompleted);
            if (!_disposed && !observer.IsCompleted)
            {
                _lateObservers.Add(observer);
            }
        }
    }

    private async Task ObserveLateCommandAsync(
        DeviceCapabilityKey key,
        Guid commandId,
        ICapabilityPublisher client,
        Task<CapabilityCommandResult> completion)
    {
        CapabilityCommandResult result;
        try
        {
            result = await completion.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            Log.Info($"Late {_label} command result dropped: capability={key}, command={commandId}; "
                     + "the router was disposed.");
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Late {_label} command failed: capability={key}, command={commandId}", ex);
            return;
        }

        bool ignore;
        lock (_gate)
        {
            ignore = !_connected
                     || !ReferenceEquals(_client, client)
                     || result.CommandId != commandId;
        }

        if (ignore)
        {
            Log.Warn($"Late {_label} command result ignored: command={result.CommandId}, expected={commandId}; "
                     + "the active publisher or control layout changed.");
            return;
        }

        ReconcileResult(key, result);
    }

    private void ReconcileResult(
        DeviceCapabilityKey key,
        CapabilityCommandResult result,
        bool terminal = true)
    {
        lock (_gate)
        {
            if (!_latestCommands.TryGetValue(key, out var latest) || latest != result.CommandId)
            {
                return;
            }

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
            result.Outcome.IsApplied() || result.Outcome == CommandOutcome.Accepted ? LogLevel.Info : LogLevel.Warn);
        Publish();
    }

    /// <summary>Whether the accepted descriptor set holds a capability matching a condition.</summary>
    /// <param name="match">The condition on the descriptor.</param>
    /// <returns>True when any accepted descriptor matches.</returns>
    /// <remarks>Reads descriptors only, so it builds no view and resolves no desired value.</remarks>
    internal bool HasDescriptor(Func<CapabilityDescriptor, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (_gate)
        {
            foreach (var (_, descriptor) in _orderedDescriptors)
            {
                if (match(descriptor))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal DeviceCapabilityView? TryGetView(DeviceCapabilityKey key)
    {
        lock (_gate)
        {
            return _descriptors.TryGetValue(key, out var descriptor)
                ? BuildViewUnderGate(key, descriptor, _utcNow())
                : null;
        }
    }

    private List<DeviceCapabilityView> BuildSnapshotUnderGate(DateTimeOffset now)
    {
        List<DeviceCapabilityView> views = [];
        foreach (var (key, descriptor) in _orderedDescriptors)
        {
            views.Add(BuildViewUnderGate(key, descriptor, now));
        }

        return views;
    }

    private DeviceCapabilityView BuildViewUnderGate(DeviceCapabilityKey key,
        CapabilityDescriptor descriptor, DateTimeOffset now)
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
                now);
        }

        var desired = ResolveDesired(key, descriptor.ProfileScope);
        var global = _globalDesired.GetValueOrDefault(key);
        var outOfRange = (desired.Value is not null
                          && !CapabilityValueValidation.ValueMatches(desired.Value, descriptor, out _))
                         || (global is not null
                             && descriptor.ProfileScope is not CapabilityProfileScope.Switched
                             && !CapabilityValueValidation.ValueMatches(global, descriptor, out _));
        _pendingValues.TryGetValue(key, out var pending);
        _lastResults.TryGetValue(key, out var result);
        _lastCommandValues.TryGetValue(key, out var commanded);
        return new DeviceCapabilityView(
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
            commanded) { Publisher = _publisher };
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

            snapshot = BuildSnapshotUnderGate(_utcNow());
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
            CommandOutcome.Applied or CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified =>
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
        DateTimeOffset now)
    {
        // A faulted capability is already saying something stronger than "old". Downgrading it to
        // stale would lose the fault.
        if (state.Quality is HardwareStateQuality.Faulted or HardwareStateQuality.Unknown)
        {
            return state;
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
    /// <param name="state">Published availability and evidence quality after freshness projection.</param>
    /// <returns>True when available and neither stale nor faulted; unknown readback alone does not disable writes.</returns>
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
    /// <summary>Validates unique identities, layout, power presets, and power-pair structure.</summary>
    /// <param name="set">Complete replacement descriptor set supplied by the current publisher.</param>
    /// <param name="error">Null on success, otherwise the first structural or semantic rejection reason.</param>
    /// <returns>Whether the set may enter host state; this does not test hardware support or native transport.</returns>
    internal static bool TryValidateDescriptorSet(
        CapabilityDescriptorSet set,
        out string? error)
    {
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

    /// <summary>Checks identity and observed-value compatibility before accepting a capability state.</summary>
    /// <param name="state">Candidate observation; verified quality requires a nonnull readback value.</param>
    /// <param name="descriptor">Accepted descriptor defining the value shape and range.</param>
    /// <param name="error">Null on success, otherwise the rejection reason.</param>
    /// <returns>Whether these structural checks pass; the publisher still owns the evidence behind its quality.</returns>
    internal static bool TryValidateState(
        CapabilityState state,
        CapabilityDescriptor descriptor,
        out string? error)
    {
        if (state.ObservedValue is not null
            && !CapabilityValueValidation.ValueMatches(state.ObservedValue, descriptor, out error))
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
                or CapabilityRole.VariableRefreshRate => kind is CapabilityValueKind.Boolean,
            CapabilityRole.ChargeBypass or CapabilityRole.ChargeProtectionMode =>
                kind is CapabilityValueKind.Boolean or CapabilityValueKind.Choice,
            CapabilityRole.GenericChoice
                or CapabilityRole.ScenarioMode
                or CapabilityRole.FanMode
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
}
