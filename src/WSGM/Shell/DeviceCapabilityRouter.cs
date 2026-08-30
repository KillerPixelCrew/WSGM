using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Shell;

/// <summary>Stable dictionary key for one semantic capability instance.</summary>
internal readonly record struct DeviceCapabilityKey(string CapabilityId, string? InstanceId)
{
    public override string ToString() => InstanceId is { Length: > 0 }
        ? $"{CapabilityId}#{InstanceId}"
        : CapabilityId;
}

/// <summary>One immutable router snapshot suitable for an overlay or diagnostics client.</summary>
internal sealed record DeviceCapabilityView(
    CapabilityDescriptor Descriptor,
    CapabilityProjection Projection,
    CapabilityCommandResult? LastResult);

/// <summary>
/// Validates and projects the semantic capability stream owned by one DeviceHost generation.
/// </summary>
internal sealed class DeviceCapabilityRouter : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Action<Action> _postToUi;
    private readonly Dictionary<DeviceCapabilityKey, CapabilityDescriptor> _descriptors = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _temporaryDesired = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityCommandResult> _lastResults = [];
    private readonly Dictionary<DeviceCapabilityKey, CapabilityValue> _pendingValues = [];

    /// <summary>Last logged availability per capability, so only changes are written.</summary>
    private readonly Dictionary<DeviceCapabilityKey, bool> _availability = [];
    private readonly Dictionary<DeviceCapabilityKey, SemaphoreSlim> _commandGates = [];
    private readonly Dictionary<Guid, DeviceCapabilityKey> _timedOutCommands = [];
    private CapabilityStateTracker _states;
    private DeviceHostClient? _client;
    private DeviceDesiredProfile? _desiredProfile;
    private string? _hardwareProfileId;
    private string? _applicationId;
    private long _descriptorGeneration;
    private long _cycleGeneration;
    private bool _onAcPower = true;
    private bool _connected;
    private bool _disposed;

    internal DeviceCapabilityRouter(long cycleGeneration, Action<Action> postToUi)
    {
        ArgumentNullException.ThrowIfNull(postToUi);
        _cycleGeneration = cycleGeneration;
        _states = new CapabilityStateTracker(cycleGeneration);
        _postToUi = postToUi;
    }

    /// <summary>Raised on the UI dispatcher with a complete immutable projection.</summary>
    internal event Action<IReadOnlyList<DeviceCapabilityView>>? Changed;

    internal void Attach(DeviceHostClient client, long cycleGeneration)
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
            _states.ResetTo(cycleGeneration);
            _lastResults.Clear();
            _pendingValues.Clear();
            _timedOutCommands.Clear();
            _connected = true;
            client.DescriptorSetReceived += OnDescriptorSet;
            client.CapabilityStateReceived += OnStateDelta;
            client.LateCommandResultReceived += OnLateCommandResult;
        }

        Publish();
    }

    internal void UpdateDesiredContext(
        DeviceDesiredProfile? desiredProfile,
        bool onAcPower,
        string? hardwareProfileId,
        string? applicationId)
    {
        lock (_gate)
        {
            if (!string.Equals(
                    _desiredProfile?.DeviceIdentityKey,
                    desiredProfile?.DeviceIdentityKey,
                    StringComparison.Ordinal)
                || !string.Equals(_hardwareProfileId, hardwareProfileId, StringComparison.Ordinal)
                || !string.Equals(_applicationId, applicationId, StringComparison.Ordinal))
            {
                _temporaryDesired.Clear();
            }

            _desiredProfile = desiredProfile;
            _onAcPower = onAcPower;
            _hardwareProfileId = hardwareProfileId;
            _applicationId = applicationId;
        }

        Publish();
    }

    internal void SetTemporaryDesired(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value)
    {
        DeviceCapabilityKey key = new(capabilityId, instanceId);
        lock (_gate)
        {
            if (value is null)
            {
                _temporaryDesired.Remove(key);
            }
            else
            {
                _temporaryDesired[key] = value;
            }
        }

        Publish();
    }

    internal void ClearTemporaryDesired()
    {
        lock (_gate)
        {
            _temporaryDesired.Clear();
        }

        Publish();
    }

    internal async Task<CapabilityCommandResult> ExecuteAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        DeviceCapabilityKey key = new(capabilityId, instanceId);
        SemaphoreSlim commandGate;
        lock (_gate)
        {
            if (!_commandGates.TryGetValue(key, out commandGate!))
            {
                commandGate = new SemaphoreSlim(1, 1);
                _commandGates.Add(key, commandGate);
            }
        }

        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CapabilityCommand command;
            DeviceHostClient client;
            CapabilityCommandResult? refusal = PrepareCommand(key, value, timeout, out command, out client);
            if (refusal is not null)
            {
                ReconcileResult(key, refusal);
                return refusal;
            }

            Publish();
            CapabilityCommandResult result;
            try
            {
                result = await client.ExecuteCommandAsync(command, cancellationToken)
                    .ConfigureAwait(false);
                if (result.CommandId != command.CommandId)
                {
                    result = Uncertain(command, "DeviceHost returned a different command ID.");
                }
            }
            catch (OperationCanceledException)
            {
                bool timedOut = DateTimeOffset.UtcNow >= command.Deadline;
                result = new CapabilityCommandResult
                {
                    CommandId = command.CommandId,
                    Outcome = timedOut ? CommandOutcome.TimedOut : CommandOutcome.Indeterminate,
                    Reason = new CapabilityReason(
                        CapabilityReasonCode.Quiescing,
                        timedOut ? "The command deadline expired." : "The command was cancelled.",
                        Retryable: true),
                    CompletedAt = DateTimeOffset.UtcNow,
                };
                lock (_gate)
                {
                    _timedOutCommands[command.CommandId] = key;
                }

                _ = CancelBestEffortAsync(client, command.CommandId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = Uncertain(command, ex.Message);
            }

            ReconcileResult(key, result);
            return result;
        }
        finally
        {
            commandGate.Release();
        }
    }

    internal IReadOnlyList<DeviceCapabilityView> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            return BuildSnapshotUnderGate(now);
        }
    }

    internal void MarkCycleGenerationChanged(long cycleGeneration)
    {
        lock (_gate)
        {
            _cycleGeneration = cycleGeneration;
            _temporaryDesired.Clear();
            _pendingValues.Clear();
            _lastResults.Clear();
        }

        Publish();
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
            _temporaryDesired.Clear();
            _pendingValues.Clear();
        }

        Publish();
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
            foreach (SemaphoreSlim commandGate in _commandGates.Values)
            {
                commandGate.Dispose();
            }

            _commandGates.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private CapabilityCommandResult? PrepareCommand(
        DeviceCapabilityKey key,
        CapabilityValue? value,
        TimeSpan timeout,
        out CapabilityCommand command,
        out DeviceHostClient client)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid commandId = Guid.NewGuid();
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
                Deadline = now.Add(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(5)),
            };

            if (!_connected || _client is null)
            {
                client = null!;
                return Reject(command, CapabilityReasonCode.HostUnavailable,
                    "DeviceHost is not connected.", retryable: true);
            }

            client = _client;
            if (!_descriptors.TryGetValue(key, out CapabilityDescriptor? descriptor))
            {
                return Reject(command, CapabilityReasonCode.Unsupported,
                    "The capability is not present in the current descriptor set.");
            }

            CapabilityState? rawState = _states.Latest(key.CapabilityId, key.InstanceId);
            if (rawState is null)
            {
                return Reject(command, CapabilityReasonCode.ObservationExpired,
                    "No current capability state has been observed.", retryable: true);
            }

            CapabilityState state = CapabilityFreshness.Evaluate(
                rawState,
                FreshnessFor(descriptor.Role),
                now,
                _cycleGeneration);
            if (!CapabilityFreshness.CanCommand(state))
            {
                return Reject(
                    command,
                    state.Reason?.Code ?? CapabilityReasonCode.ObservationExpired,
                    state.Reason?.Detail ?? "Capability state is not current.",
                    retryable: state.Reason?.Retryable ?? true);
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
                && !DeviceCapabilityValidation.ValueMatches(value, descriptor, out string? error))
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
            }

            return null;
        }
    }

    private void OnDescriptorSet(CapabilityDescriptorSet descriptors)
    {
        lock (_gate)
        {
            if (!DeviceCapabilityValidation.TryValidateDescriptorSet(
                descriptors,
                _cycleGeneration,
                _descriptorGeneration,
                out string? error))
            {
                Log.Warn($"Device descriptor set rejected: {error}");
                return;
            }

            _descriptorGeneration = descriptors.Generation;
            _descriptors.Clear();
            foreach (CapabilityDescriptor descriptor in descriptors.Descriptors)
            {
                _descriptors.Add(Key(descriptor), descriptor);
            }

            _states = new CapabilityStateTracker(_cycleGeneration);
            _pendingValues.Clear();
            _lastResults.Clear();
            _temporaryDesired.Clear();
        }

        Publish();
    }

    private void OnStateDelta(CapabilityStateDelta delta)
    {
        lock (_gate)
        {
            DeviceCapabilityKey key = Key(delta.State);
            string? error = null;
            if (delta.Sequence <= 0
                || !_descriptors.TryGetValue(key, out CapabilityDescriptor? descriptor)
                || !DeviceCapabilityValidation.TryValidateState(
                    delta.State,
                    descriptor,
                    _descriptorGeneration,
                    _cycleGeneration,
                    out error))
            {
                Log.Warn($"Device capability state rejected: key={key}, "
                    + $"{error ?? "invalid sequence or key"}");
                return;
            }

            DeltaRejection rejection = _states.Apply(delta);
            if (rejection is not DeltaRejection.None)
            {
                Log.Warn($"Device capability delta rejected: key={key}, reason={rejection}.");
                return;
            }

            LogAvailabilityChange(key, delta.State);
        }

        Publish();
    }

    /// <summary>Logs a capability becoming available or unavailable, with the plugin's own reason.</summary>
    /// <param name="key">The capability that changed.</param>
    /// <param name="state">The state just applied.</param>
    /// <remarks>
    /// The plugin already says exactly why a capability is unavailable — a gated firmware revision,
    /// a missing prerequisite, a topology it could not match — and WSGM was throwing every one of
    /// those away. A device reporting itself "partly available" with no record of which parts or
    /// why cannot be diagnosed from a pasted log, which is the only way most of these devices are
    /// reachable. Logged on change so a capability that is simply unavailable does not repeat.
    /// <para>
    /// Called under <c>_gate</c>, after the delta is accepted, so what is logged is what was
    /// actually applied rather than what arrived.
    /// </para>
    /// </remarks>
    private void LogAvailabilityChange(DeviceCapabilityKey key, CapabilityState state)
    {
        bool previous = _availability.TryGetValue(key, out bool known) && known;
        bool first = !_availability.ContainsKey(key);
        _availability[key] = state.Available;
        if (!first && previous == state.Available)
        {
            return;
        }

        if (state.Available)
        {
            Log.Info($"Device capability available: {key}.");
            return;
        }

        string reason = state.Reason?.Detail is { Length: > 0 } detail
            ? $"{state.Reason.Code}: {detail}"
            : state.Reason?.Code.ToString() ?? "no reason given";
        Log.Warn($"Device capability unavailable: {key} — {reason}");
    }

    private void OnLateCommandResult(CapabilityCommandResult result)
    {
        DeviceCapabilityKey key;
        lock (_gate)
        {
            if (!_timedOutCommands.Remove(result.CommandId, out key))
            {
                Log.Warn($"Uncorrelated late device command result ignored: command={result.CommandId}.");
                return;
            }
        }

        Log.Info($"Late device command result reconciled: command={result.CommandId}, "
            + $"capability={key}, outcome={result.Outcome}.");
        ReconcileResult(key, result);
    }

    private void ReconcileResult(DeviceCapabilityKey key, CapabilityCommandResult result)
    {
        lock (_gate)
        {
            _pendingValues.Remove(key);
            _lastResults[key] = result;
            _timedOutCommands.Remove(result.CommandId);
        }

        Log.Info($"Device command: capability={key}, command={result.CommandId}, "
            + $"outcome={result.Outcome}, rollback={result.Rollback}.");
        Publish();
    }

    private IReadOnlyList<DeviceCapabilityView> BuildSnapshotUnderGate(DateTimeOffset now)
    {
        List<DeviceCapabilityView> views = [];
        foreach ((DeviceCapabilityKey key, CapabilityDescriptor descriptor) in _descriptors
            .OrderBy(item => item.Key.CapabilityId, StringComparer.Ordinal)
            .ThenBy(item => item.Key.InstanceId, StringComparer.Ordinal))
        {
            CapabilityState state = _states.Latest(key.CapabilityId, key.InstanceId)
                ?? UnknownState(key);
            if (!_connected)
            {
                state = state with
                {
                    Available = false,
                    Quality = HardwareStateQuality.Stale,
                    Reason = new CapabilityReason(
                        CapabilityReasonCode.HostUnavailable,
                        "DeviceHost is disconnected.",
                        Retryable: true),
                };
            }
            else
            {
                state = CapabilityFreshness.Evaluate(
                    state,
                    FreshnessFor(descriptor.Role),
                    now,
                    _cycleGeneration);
            }

            ResolvedDeviceDesiredValue desired = ResolveDesired(key);
            bool outOfRange = desired.Value is not null
                && !DeviceCapabilityValidation.ValueMatches(desired.Value, descriptor, out _);
            _pendingValues.TryGetValue(key, out CapabilityValue? pending);
            _lastResults.TryGetValue(key, out CapabilityCommandResult? result);
            views.Add(new DeviceCapabilityView(
                descriptor,
                new CapabilityProjection
                {
                    State = state,
                    DesiredValue = desired.Value,
                    DesiredSource = MapSource(desired.Source),
                    PendingValue = pending,
                    Progress = Progress(pending, result),
                    DesiredValueOutOfRange = outOfRange,
                },
                result));
        }

        return views;
    }

    private ResolvedDeviceDesiredValue ResolveDesired(DeviceCapabilityKey key)
    {
        _temporaryDesired.TryGetValue(key, out CapabilityValue? temporary);
        DeviceCapabilityPreference? preference = _desiredProfile?.Capabilities.FirstOrDefault(
            item => string.Equals(item.CapabilityId, key.CapabilityId, StringComparison.Ordinal)
                && string.Equals(item.InstanceId, key.InstanceId, StringComparison.Ordinal));
        return preference is null
            ? new ResolvedDeviceDesiredValue(
                temporary,
                temporary is null
                    ? DeviceDesiredValueSource.None
                    : DeviceDesiredValueSource.TemporaryRequest)
            : DeviceDesiredStateResolver.Resolve(
                preference,
                _onAcPower,
                _hardwareProfileId,
                _applicationId,
                temporary);
    }

    private CapabilityState UnknownState(DeviceCapabilityKey key) => new()
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
            Retryable: true),
    };

    private void DetachUnderGate()
    {
        if (_client is not null)
        {
            _client.DescriptorSetReceived -= OnDescriptorSet;
            _client.CapabilityStateReceived -= OnStateDelta;
            _client.LateCommandResultReceived -= OnLateCommandResult;
        }

        _client = null;
        _connected = false;
    }

    private void Publish()
    {
        IReadOnlyList<DeviceCapabilityView> snapshot = Snapshot(DateTimeOffset.UtcNow);
        _postToUi(() => Changed?.Invoke(snapshot));
    }

    private static async Task CancelBestEffortAsync(DeviceHostClient client, Guid commandId)
    {
        try
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(2));
            await client.CancelCommandAsync(commandId, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Device command cancellation was unverified: command={commandId}, {ex.Message}");
        }
    }

    private static CapabilityCommandResult Reject(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        bool retryable = false) => new()
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(code, detail, retryable),
            CompletedAt = DateTimeOffset.UtcNow,
        };

    private static CapabilityCommandResult Uncertain(CapabilityCommand command, string detail) => new()
    {
        CommandId = command.CommandId,
        Outcome = CommandOutcome.Indeterminate,
        Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable, detail, Retryable: true),
        CompletedAt = DateTimeOffset.UtcNow,
    };

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
            _ => CommandProgress.Idle,
        };
    }

    private static DesiredValueSource MapSource(DeviceDesiredValueSource source) => source switch
    {
        DeviceDesiredValueSource.GlobalDefault => DesiredValueSource.GlobalDefault,
        DeviceDesiredValueSource.PowerPolicy => DesiredValueSource.PowerSourcePolicy,
        DeviceDesiredValueSource.HardwareProfile => DesiredValueSource.HardwareProfile,
        DeviceDesiredValueSource.ApplicationOverride => DesiredValueSource.ApplicationOverride,
        DeviceDesiredValueSource.TemporaryRequest => DesiredValueSource.TemporaryRequest,
        _ => DesiredValueSource.None,
    };

    private static FreshnessPolicy FreshnessFor(CapabilityRole role) => role switch
    {
        CapabilityRole.Telemetry or CapabilityRole.FanMeasuredRpm => FreshnessPolicy.Telemetry,
        CapabilityRole.ChargeLimit
            or CapabilityRole.ChargeProtectionMode
            or CapabilityRole.ChargeBypass
            or CapabilityRole.LightingPower
            or CapabilityRole.LightingBrightness
            or CapabilityRole.LightingZoneColor
            or CapabilityRole.LightingEffect
            or CapabilityRole.LightingEffectSpeed => FreshnessPolicy.Settings,
        _ => FreshnessPolicy.Control,
    };

    private static DeviceCapabilityKey Key(CapabilityDescriptor descriptor) =>
        new(descriptor.CapabilityId, descriptor.InstanceId);

    private static DeviceCapabilityKey Key(CapabilityState state) =>
        new(state.CapabilityId, state.InstanceId);
}

/// <summary>Structural and semantic validation applied before plugin data enters WSGM state.</summary>
internal static class DeviceCapabilityValidation
{
    private const int MaxDescriptors = 128;
    private const int MaxChoices = 64;

    /// <summary>Ceiling a text descriptor's own maximum length may declare.</summary>
    private const int MaxTextLength = 256;
    private const int MaxIdLength = 128;

    /// <summary>
    /// Matches <see cref="PluginSettingSection.MaxSectionIdLength"/>: a capability's section names
    /// the same declared section a setting does, so a longer id here would name nothing.
    /// </summary>
    private const int MaxSectionIdLength = PluginSettingSection.MaxSectionIdLength;

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

        if (set.Descriptors.Count > MaxDescriptors)
        {
            error = $"Descriptor set exceeds {MaxDescriptors} entries.";
            return false;
        }

        HashSet<DeviceCapabilityKey> keys = [];
        foreach (CapabilityDescriptor descriptor in set.Descriptors)
        {
            if (!TryValidateDescriptor(descriptor, out error)
                || !keys.Add(new DeviceCapabilityKey(
                    descriptor.CapabilityId,
                    descriptor.InstanceId)))
            {
                error ??= "Descriptor keys are duplicated.";
                return false;
            }
        }

        error = null;
        return true;
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

        bool valid = value.Kind switch
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
            CapabilityValueKind.Curve => CurveIsValid(value.CurveValue),
            CapabilityValueKind.Text => PlainText.TryValidate(
                value.TextValue,
                descriptor.MaximumLength ?? 0,
                "text",
                out _),
            CapabilityValueKind.None => false,
            _ => false,
        };
        error = valid ? null : "Capability value violates its descriptor shape or bounds.";
        return valid;
    }

    private static bool TryValidateDescriptor(CapabilityDescriptor descriptor, out string? error)
    {
        if (!ValidId(descriptor.CapabilityId, MaxIdLength)
            || (descriptor.InstanceId is not null && !ValidId(descriptor.InstanceId, 64)))
        {
            error = "Capability or instance ID is invalid.";
            return false;
        }

        if (!descriptor.Display.TryValidate(out error))
        {
            return false;
        }

        if (descriptor.SectionId is { } sectionId)
        {
            if (!descriptor.Role.IsGeneric())
            {
                // Named in the error, because from the plugin author's side this looks like a
                // section that was simply ignored.
                error =
                    $"Capability role {descriptor.Role} may not declare a section: a semantic role "
                    + "keeps the placement WSGM gives it on every device.";
                return false;
            }

            if (!ValidId(sectionId, MaxSectionIdLength))
            {
                error = "Capability section ID is invalid.";
                return false;
            }
        }

        if (!descriptor.SupportsRead && !descriptor.SupportsWrite && !descriptor.SupportsAction)
        {
            error = "Descriptor exposes no readable, writable, or actionable operation.";
            return false;
        }

        if (descriptor.ValueKind is CapabilityValueKind.None != descriptor.SupportsAction
            || descriptor.ValueKind is CapabilityValueKind.None
                && (descriptor.SupportsRead || descriptor.SupportsWrite))
        {
            error = "Action and value-bearing descriptor shapes are inconsistent.";
            return false;
        }

        if (descriptor.ValueKind is CapabilityValueKind.Integer
            && (descriptor.Minimum is null
                || descriptor.Maximum is null
                || descriptor.Minimum > descriptor.Maximum
                || descriptor.Step is null or <= 0))
        {
            error = "Integer descriptors require an ordered range and positive step.";
            return false;
        }

        if (descriptor.ValueKind is CapabilityValueKind.Choice
            && (descriptor.Choices.Count is 0 or > MaxChoices
                || descriptor.Choices.Any(choice => !ValidId(choice.Value, 64))
                || descriptor.Choices.Select(choice => choice.Value).Distinct(StringComparer.Ordinal)
                    .Count() != descriptor.Choices.Count))
        {
            error = "Choice descriptor values are empty, invalid, oversized, or duplicated.";
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
            && descriptor.MaximumLength is not (> 0 and <= MaxTextLength))
        {
            error = $"Text descriptors require a maximumLength between 1 and {MaxTextLength}.";
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

    private static bool RoleMatchesValueKind(CapabilityRole role, CapabilityValueKind kind) => role switch
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
        _ => true,
    };

    private static bool CurveIsValid(IReadOnlyList<CurvePoint> points)
    {
        if (points.Count is 0 or > 64)
        {
            return false;
        }

        for (int index = 1; index < points.Count; index++)
        {
            if (points[index].Input <= points[index - 1].Input)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidId(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');
}
