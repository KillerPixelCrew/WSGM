// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.Gpu;

/// <summary>Driver-discovered presentation and controls owned by one live native session.</summary>
/// <param name="Sections">Capability groups to publish through the host.</param>
/// <param name="Controls">Native control adapters; use only while their session remains alive.</param>
internal sealed record DriverModel(IReadOnlyList<CapabilitySection> Sections, IReadOnlyList<DriverControl> Controls);

/// <summary>A native driver session accessed exclusively through DriverRuntime's serialized lane.</summary>
internal interface IDriverSession : IDisposable
{
    /// <summary>Starts one observation pass or command; the session may load driver state once for it.</summary>
    void BeginPass();

    /// <summary>Discovers the control model for the open driver session.</summary>
    /// <returns>Sections and adapters whose native resources remain owned by this session.</returns>
    DriverModel Discover();

    /// <summary>Applies one host-owned application profile synchronization request.</summary>
    /// <param name="sync">Desired profile policy and ownership records.</param>
    /// <param name="admission">Checked immediately before each native setter.</param>
    /// <param name="token">Cancels preparatory work; a dispatched write must not be retried.</param>
    /// <returns>Accepted, rejected or uncertain profile work and retained recovery state.</returns>
    ApplicationProfileSyncResult Sync(ApplicationProfileSync sync, WriteAdmission admission, CancellationToken token);
}

/// <summary>Classifies a native driver failure for command result and session recovery decisions.</summary>
/// <param name="message">Diagnostic detail safe for the host log.</param>
/// <param name="attempted">Whether a write may already have reached the driver.</param>
/// <param name="lost">Whether the native session is no longer usable.</param>
internal sealed class DriverFailure(string message, bool attempted = false, bool lost = false) : Exception(message)
{
    /// <summary>Whether at least one setter may have reached the driver; false does not classify session health.</summary>
    internal bool Attempted { get; } = attempted;

    /// <summary>Whether the session is unusable and must retire before reopening.</summary>
    internal bool Lost { get; } = lost;
}

/// <summary>One driver's capability adapter; policy, serialization and publication remain in DriverRuntime.</summary>
/// <param name="descriptor">Stable capability identity, range and presentation metadata.</param>
internal abstract class DriverControl(CapabilityDescriptor descriptor)
{
    /// <summary>Stable descriptor used for command validation and host publication.</summary>
    internal CapabilityDescriptor Descriptor { get; } = descriptor;

    /// <summary>Capability and instance identifiers joined for the session's internal lookup.</summary>
    internal string Key => Descriptor.CapabilityId + "/" + Descriptor.InstanceId;

    /// <summary>Support-probe cache key; adapters may share a probe result across related controls.</summary>
    internal virtual string SupportKey => Key;

    /// <summary>Reads the control from its live native session on the runtime's serialized lane.</summary>
    /// <returns>The observed typed value; failures throw rather than fabricate a known value.</returns>
    internal abstract CapabilityValue Read();

    /// <summary>Attempts one native setter after checking the supplied write admission.</summary>
    /// <param name="value">Descriptor-compatible requested value.</param>
    /// <param name="admission">Deadline/cancellation gate checked immediately before native mutation.</param>
    /// <remarks>Returning does not independently confirm hardware state; the runtime performs readback separately.</remarks>
    internal abstract void Write(CapabilityValue value, WriteAdmission admission);

    /// <summary>Probes setter support by writing the current value through the native setter.</summary>
    /// <param name="current">Value just read from this control.</param>
    /// <param name="admission">Gate checked before the probe write; this operation is not read-only.</param>
    internal virtual void ProbeSupport(CapabilityValue current, WriteAdmission admission)
    {
        Write(current, admission);
    }

    /// <summary>Checks the supported Boolean, choice, or integer command shape against this control's descriptor.</summary>
    /// <param name="value">Requested value, or null for a descriptor declaring an action.</param>
    /// <returns>Whether this adapter accepts the shape, range, and step; hardware admission is checked later.</returns>
    internal bool Accepts(CapabilityValue? value)
    {
        if (Descriptor.SupportsAction)
        {
            return value is null;
        }

        if (value is null || value.Kind != Descriptor.ValueKind || !Descriptor.SupportsWrite)
        {
            return false;
        }

        return value.Kind switch
        {
            CapabilityValueKind.Boolean => value.BooleanValue is not null,
            CapabilityValueKind.Choice => Descriptor.Choices.Any(choice => choice.Value == value.ChoiceValue),
            CapabilityValueKind.Integer => value.IntegerValue is { } number && number >= Descriptor.Minimum
                                                                            && number <= Descriptor.Maximum &&
                                                                            (number - Descriptor.Minimum) %
                                                                            (Descriptor.Step ?? 1) == 0,
            CapabilityValueKind.None => true,
            _ => false
        };
    }
}

/// <summary>Serialized resident driver ownership shared by the AMD and NVIDIA package assemblies.</summary>
/// <param name="id">Stable common-plugin id.</param>
/// <param name="open">Opens a session over the host state directory and change logger.</param>
internal sealed class DriverRuntime(string id, Func<string, Action<string, string>, IDriverSession> open)
    : IPlugin, ICapabilityPlugin
{
    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly Dictionary<string, (CapabilityValue? Value, DateTimeOffset At)> _published = [];
    private readonly Lock _stopGate = new();

    /// <summary>Probe outcomes by support key: null when the round trip succeeded, else why it failed.</summary>
    private readonly Dictionary<string, string?> _supportResults = [];

    private ICapabilityHost? _capabilities;
    private PluginContext? _context;
    private long _cycle;
    private bool _disposed;
    private string? _fingerprint;
    private long _generation;
    private IPluginHost? _host;
    private Task? _loop;
    private DriverModel _model = new([], []);
    private CancellationTokenSource? _observation;
    private Task? _retiring;
    private volatile bool _running;
    private IDriverSession? _session;

    /// <inheritdoc />
    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                "The command was cancelled before its driver write.", true);
        }

        try
        {
            if (!_running || _session is not { } session)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.HostUnavailable,
                    "The GPU driver is not open.");
            }

            if (command.ExpectedCycleGeneration != _cycle || command.ExpectedDescriptorGeneration != _generation)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.GenerationChanged,
                    "The command was authored against an earlier descriptor set.", true);
            }

            if (command.Deadline.HasExpired)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                    "Command deadline passed before it could be applied.", true);
            }

            var control = _model.Controls.FirstOrDefault(item => item.Descriptor.CapabilityId == command.CapabilityId
                                                                 && item.Descriptor.InstanceId == command.InstanceId);
            if (control is null)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                    $"{command.CapabilityId} is not published for {command.InstanceId}.");
            }

            if (!control.Accepts(command.RequestedValue))
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                    "The value is not offered by this GPU control.");
            }

            var requested = command.RequestedValue ?? CapabilityValue.None();
            var admission = new WriteAdmission(cancellationToken, command.Deadline,
                () => _running && _session is not null);
            try
            {
                // Admission may originate on the UI dispatcher. All native work stays off it.
                await Task.Run(() =>
                {
                    session.BeginPass();
                    control.Write(requested, admission);
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DriverFailure failure)
            {
                if (failure.Lost)
                {
                    await LoseAsync(failure.Message).ConfigureAwait(false);
                }

                if (failure.Attempted)
                {
                    return CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
                        failure.Message, RollbackResult.NotRequired);
                }

                if (!admission.Admitted)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing, failure.Message, true);
                }

                return CommandResults.Rejected(command,
                    failure.Lost ? CapabilityReasonCode.HostUnavailable : CapabilityReasonCode.Unsupported,
                    failure.Message);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, error.Message,
                    RollbackResult.NotRequired);
            }

            CapabilityValue? readback = null;
            if (control.Descriptor.SupportsRead)
            {
                try
                {
                    readback = await Task.Run(control.Read, CancellationToken.None).ConfigureAwait(false);
                }
                catch (DriverFailure failure) when (failure.Lost)
                {
                    await LoseAsync(failure.Message).ConfigureAwait(false);
                    return CommandResults.Unverified(command,
                        "The driver accepted the write and disappeared before readback: " + failure.Message);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Trace(DeviceTraceLevel.Warn, error.Message);
                }
            }

            // The written value is what is published, verified or not.
            var verified = readback is not null && readback == requested;
            await PublishValueAsync(control, requested, CancellationToken.None,
                    quality: verified ? HardwareStateQuality.Verified : HardwareStateQuality.Observed)
                .ConfigureAwait(false);
            return verified
                ? CommandResults.Verified(command, readback!)
                : CommandResults.Unverified(command);
        }
        finally
        {
            await ReleaseLaneAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(ApplicationProfileSync sync,
        CancellationToken cancellationToken)
    {
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running || _session is not { } session || sync.CycleGeneration != _cycle)
            {
                return new ApplicationProfileSyncResult(0, 0, []);
            }

            var admission = new WriteAdmission(cancellationToken, Deadline.Never,
                () => _running && _session is not null);
            try
            {
                return await Task.Run(() => session.Sync(sync, admission, cancellationToken), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (DriverFailure failure) when (failure.Lost)
            {
                await LoseAsync(failure.Message).ConfigureAwait(false);
                return new ApplicationProfileSyncResult(0, 0,
                    [new ApplicationProfileFailure("driver", "", "", failure.Message)]);
            }
        }
        finally
        {
            await ReleaseLaneAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public string Id => id;

    /// <inheritdoc />
    public async ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _retiring) is { } retiring)
        {
            await retiring.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return _session is null || _model.Controls.Count == 0 ? PluginHealth.Unavailable : PluginHealth.Ready;
            }

            _host = host;
            _context = context;
            _capabilities = host.Capabilities ??
                            throw new InvalidOperationException("The host supplied no GPU capability channel.");
            _cycle = _capabilities.CycleGeneration;
            _fingerprint = null;
            _running = true;
            await Task.Run(() => ObserveAsync(cancellationToken, context.Deadline), cancellationToken)
                .ConfigureAwait(false);
            if (_running)
            {
                _observation = new CancellationTokenSource();
                _loop = ObserveLoopAsync(_observation.Token);
            }

            return _session is null || _model.Controls.Count == 0 || !_running
                ? PluginHealth.Unavailable
                : PluginHealth.Ready;
        }
        catch
        {
            _running = false;
            throw;
        }
        finally
        {
            await ReleaseLaneAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken)
    {
        await StopAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        await StartAsync(_host!, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops within the caller's budget.</summary>
    /// <remarks>
    ///     When the token ends first, stop reports itself unconfirmed: the retiring task keeps the native lane
    ///     and the DLL until the running driver call returns, then closes them.
    /// </remarks>
    /// <param name="context">Lifecycle context supplied by the host; retirement does not use it.</param>
    /// <param name="cancellationToken">Bounds the caller's wait; does not cancel an in-flight native call.</param>
    /// <returns>True when retirement completed; false when the wait was cancelled and cleanup continues.</returns>
    public async ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        _running = false;
        var retiring = BeginRetirement();
        try
        {
            await retiring.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Trace(DeviceTraceLevel.Warn, "The driver is still finishing an owned call; cleanup remains queued.");
            return false;
        }
    }

    /// <summary>Closes runtime admission and schedules native retirement without waiting for it.</summary>
    /// <returns>An already completed value task; use the stop result to determine whether release was confirmed.</returns>
    /// <remarks>Outstanding driver calls retain the session and DLL until the retirement task can close them.</remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _running = false;
        _ = BeginRetirement();
        return ValueTask.CompletedTask;
    }

    /// <summary>Starts retirement once, or returns the one already running.</summary>
    private Task BeginRetirement()
    {
        lock (_stopGate)
        {
            if (_retiring is null || _retiring.IsCompleted)
            {
                var observation = _observation;
                var loop = _loop;
                observation?.Cancel();
                _retiring = Task.Run(() => RetireAsync(observation, loop), CancellationToken.None);
            }

            return _retiring;
        }
    }

    private async Task RetireAsync(CancellationTokenSource? observation, Task? loop)
    {
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Trace(DeviceTraceLevel.Warn, "Driver observation stopped: " + error.Message);
            }
        }

        await _lane.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.Run(Close, CancellationToken.None).ConfigureAwait(false);
            if (_capabilities is not null)
            {
                try
                {
                    await PublishModelAsync(new DriverModel([], []), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Trace(DeviceTraceLevel.Warn, "Driver channel retired: " + error.Message);
                }
            }
        }
        finally
        {
            _lane.Release();
        }

        _ = Interlocked.CompareExchange(ref _loop, null, loop);
        Interlocked.CompareExchange(ref _observation, null, observation);
        observation?.Dispose();
    }

    private async Task ObserveLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await _lane.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (_running)
                    {
                        // The loop is bounded by its own token, which stop cancels.
                        await Task.Run(() => ObserveAsync(token, Deadline.Never), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    await ReleaseLaneAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>One observation pass, bounded by the token and deadline of the call that runs it.</summary>
    private async Task ObserveAsync(CancellationToken token, Deadline deadline)
    {
        try
        {
            _session ??= open(_context!.StateDirectory, (key, detail) =>
                _host!.TraceChange(DeviceTraceLevel.Warn, id, key, detail));
            _session.BeginPass();
            await PublishModelAsync(CheckSupport(_session.Discover(), token, deadline), token).ConfigureAwait(false);
            foreach (var control in _model.Controls)
            {
                try
                {
                    await PublishValueAsync(control, control.Read(), token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException &&
                                              error is not DriverFailure { Lost: true })
                {
                    _host!.TraceChange(DeviceTraceLevel.Warn, id, control.Key, error.Message);
                    await PublishValueAsync(control, null, token, error.Message).ConfigureAwait(false);
                }
            }

            Health(_model.Controls.Count == 0 ? PluginHealth.Unavailable : PluginHealth.Ready,
                _model.Controls.Count == 0
                    ? "The driver reported no supported GPU controls."
                    : "Driver controls available.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Close();
            await PublishModelAsync(new DriverModel([], []), token).ConfigureAwait(false);
            Health(PluginHealth.Unavailable, error.Message);
        }
    }

    /// <summary>Publishes a model. A set WSGM refuses keeps the session open and goes out again next pass.</summary>
    private async Task PublishModelAsync(DriverModel model, CancellationToken token)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            model.Sections, Descriptors = model.Controls.Select(control => control.Descriptor).ToArray()
        })));
        _model = model;
        if (fingerprint == _fingerprint)
        {
            return;
        }

        try
        {
            await _capabilities!.PublishDescriptorsAsync(new CapabilityDescriptorSet
            {
                CycleGeneration = _cycle, Generation = ++_generation, Sections = model.Sections,
                Descriptors = model.Controls.Select(control => control.Descriptor).ToArray()
            }, token).ConfigureAwait(false);
            _fingerprint = fingerprint;
            _published.Clear();
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            _host!.TraceChange(DeviceTraceLevel.Warn, id, "descriptors",
                "WSGM refused the descriptor set: " + error.Message);
        }
    }

    /// <summary>
    ///     Reads every writable control and probes each support key once. Only probe outcomes are kept: a failed
    ///     read skips the control for this pass, and a probe refused before its native write runs again later.
    /// </summary>
    private DriverModel CheckSupport(DriverModel discovered, CancellationToken token, Deadline deadline)
    {
        var values = new Dictionary<string, CapabilityValue>();
        // Read the whole model before any setter is exercised. Actions and status rows are never written.
        foreach (var control in discovered.Controls.Where(control => control.Descriptor.SupportsWrite
                                                                     && !control.Descriptor.SupportsAction))
        {
            token.ThrowIfCancellationRequested();
            if (_supportResults.ContainsKey(control.SupportKey))
            {
                continue;
            }

            try
            {
                values[control.Key] = control.Read();
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                if (error is DriverFailure { Lost: true })
                {
                    throw;
                }

                _host!.TraceChange(DeviceTraceLevel.Info, id, "support/" + control.SupportKey,
                    "Support read failed; control skipped this pass: " + error.Message);
            }
        }

        var admission = new WriteAdmission(token, deadline, () => _running && _session is not null);
        foreach (var control in discovered.Controls.Where(control => values.ContainsKey(control.Key)))
        {
            token.ThrowIfCancellationRequested();
            if (_supportResults.ContainsKey(control.SupportKey))
            {
                continue;
            }

            try
            {
                control.ProbeSupport(values[control.Key], admission);
                _supportResults[control.SupportKey] = null;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (error is OperationCanceledException
                    || (error is DriverFailure { Attempted: false } && !admission.Admitted))
                {
                    // Nothing was written, so nothing is recorded: the next discovery probes it again.
                    token.ThrowIfCancellationRequested();
                    throw;
                }

                // A failed or uncertain native write is never retried by the observation loop or a reconnect.
                _supportResults[control.SupportKey] = error.Message;
                if (error is DriverFailure { Lost: true })
                {
                    throw;
                }
            }

            _host!.TraceChange(DeviceTraceLevel.Info, id, "support/" + control.SupportKey,
                _supportResults[control.SupportKey] is { } detail
                    ? "Support round trip failed; control omitted: " + detail
                    : "Support round trip succeeded; current native state retained.");
        }

        return discovered with
        {
            Controls = discovered.Controls.Where(control => !control.Descriptor.SupportsWrite
                                                            || control.Descriptor.SupportsAction
                                                            || (_supportResults.TryGetValue(control.SupportKey,
                                                                out var result) && result is null)).ToArray()
        };
    }

    /// <summary>Publishes one state. A state WSGM refuses is traced and published again on the next pass.</summary>
    private async Task PublishValueAsync(DriverControl control, CapabilityValue? value, CancellationToken token,
        string? error = null, HardwareStateQuality? quality = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (_published.TryGetValue(control.Key, out var previous) && previous.Value == value
                                                                  && now - previous.At < TimeSpan.FromSeconds(20))
        {
            return;
        }

        try
        {
            await _capabilities!.PublishCapabilityStateAsync(new CapabilityState
            {
                CapabilityId = control.Descriptor.CapabilityId, InstanceId = control.Descriptor.InstanceId,
                CycleGeneration = _cycle, DescriptorGeneration = _generation, ObservedValue = value,
                Available = value is not null || control.Descriptor.SupportsWrite || control.Descriptor.SupportsAction,
                ObservedAt = now,
                Reason = error is null ? null : new CapabilityReason(CapabilityReasonCode.TransportFaulted, error),
                Quality = quality ?? (value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed)
            }, token).ConfigureAwait(false);
            _published[control.Key] = (value, now);
        }
        catch (Exception refusal) when (refusal is not OperationCanceledException and not OutOfMemoryException)
        {
            _published.Remove(control.Key);
            _host!.TraceChange(DeviceTraceLevel.Warn, id, "publish/" + control.Key,
                "WSGM refused the state: " + refusal.Message);
        }
    }

    /// <summary>Closes a lost session and retracts its controls.</summary>
    private async Task LoseAsync(string detail)
    {
        await Task.Run(Close, CancellationToken.None).ConfigureAwait(false);
        await PublishModelAsync(new DriverModel([], []), CancellationToken.None).ConfigureAwait(false);
        Health(PluginHealth.Unavailable, detail);
    }

    private async ValueTask ReleaseLaneAsync()
    {
        if (!_running)
        {
            await Task.Run(Close, CancellationToken.None).ConfigureAwait(false);
        }

        _lane.Release();
    }

    private void Close()
    {
        var session = _session;
        _session = null;
        try
        {
            session?.Dispose();
        }
        catch (Exception error)
        {
            Trace(DeviceTraceLevel.Warn, "Driver cleanup: " + error.Message);
        }
    }

    private void Trace(DeviceTraceLevel level, string message)
    {
        _host?.Trace(level, id, message);
    }

    private void Health(PluginHealth health, string detail)
    {
        if (_context is { } context)
        {
            _host!.PublishHealth(new PluginHealthPublication(context.Instance, context.Generation, health, detail));
            _host!.TraceChange(DeviceTraceLevel.Info, id, "health", detail);
        }
    }
}
