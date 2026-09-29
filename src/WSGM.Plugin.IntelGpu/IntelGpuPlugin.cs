using System.Text.Json;
using Microsoft.Win32;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Display;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.IntelGpu.Igcl;
using WSGM.Plugin.IntelGpu.Profiles;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.IntelGpu;

/// <summary>
///     Intel graphics and display controls, over Intel's Graphics Control Library.
/// </summary>
/// <remarks>
///     A <c>wsgm.gpu</c> package: it runs beside the device package and independently of device
///     integration, publishes its controls through <see cref="ICapabilityHost" />, and keeps the driver's
///     own per-application profiles in line with WSGM's game profiles. Every control is read and written
///     through IGCL except the shared GPU memory override, which only exists in the registry.
///     <para>
///         One IGCL session belongs to one plugin cycle. It opens at start and on resume, is reopened
///         when the driver reports the device lost (a driver update or reset), and closes on suspend
///         and stop. Every IGCL call runs on one lane, so reads never race writes.
///     </para>
/// </remarks>
public sealed class IntelGpuPlugin : IPlugin, ICapabilityPlugin
{
    /// <summary>How often the driver's state is read back.</summary>
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How old an unchanged state may get before it is published again. WSGM's router expires a
    ///     generic observation after 30 seconds.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20);

    /// <summary>How long stop waits for the observation loop.</summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly Dictionary<string, WrittenValue> _written = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PublishedState> _published = new(StringComparer.Ordinal);
    private IgclApi? _api;
    private ICapabilityHost? _capabilities;
    private ColorStore? _colors;
    private PluginContext? _context;
    private long _cycleGeneration;
    private long _descriptorGeneration;
    private string? _descriptorFingerprint;
    private bool _disposed;
    private IPluginHost? _host;
    private IntelLog _log = IntelLog.None;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private IntelModel? _model;
    private IgclSession? _session;
    private ApplicationProfileSynchronizer? _synchronizer;
    private bool _running;

    /// <inheritdoc />
    public string Id => "wsgm.gpu.intel";

    /// <inheritdoc />
    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running || _model is null || _capabilities is null)
            {
                return Rejected(command, CapabilityReasonCode.HostUnavailable, "The Intel driver is not open.");
            }

            if (command.ExpectedCycleGeneration != _cycleGeneration
                || command.ExpectedDescriptorGeneration != _descriptorGeneration)
            {
                return Rejected(command, new CapabilityReason(CapabilityReasonCode.GenerationChanged,
                    "The command was authored against an earlier descriptor set.", true));
            }

            if (command.Deadline.HasExpired)
            {
                return Rejected(command, new CapabilityReason(CapabilityReasonCode.Quiescing,
                    "Command deadline passed before it could be applied.", true));
            }

            if (_model.Find(command.CapabilityId, command.InstanceId) is not { } control)
            {
                return Rejected(command, CapabilityReasonCode.Unsupported,
                    $"{command.CapabilityId} is not published for {command.InstanceId}.");
            }

            if (!control.Validate(command.RequestedValue, out var error))
            {
                return Rejected(command, CapabilityReasonCode.ValueOutOfRange, error ?? "The value is not accepted.");
            }

            var requested = command.RequestedValue!;
            var write = Guard(() => control.Write(requested), control);
            switch (write.Status)
            {
                case WriteStatus.Refused:
                    _log.Warn("command", $"{Describe(control)} refused: {write.Detail}");
                    return Rejected(command, CapabilityReasonCode.Unsupported, write.Detail ?? "The driver refused.");
                case WriteStatus.Uncertain:
                    // Never retried: whether anything changed is unknown, and a second write on top of an
                    // unknown state is a guess. The next observation reports what the driver holds.
                    _log.Error("command", $"{Describe(control)} failed: {write.Detail}");
                    return new CapabilityCommandResult
                    {
                        CommandId = command.CommandId,
                        Outcome = CommandOutcome.Indeterminate,
                        Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted, write.Detail),
                        CompletedAt = DateTimeOffset.UtcNow
                    };
            }

            var readback = Guard(control.Read, control);
            var verified = control.Confirms(requested, readback.Value);
            _written[IntelModel.Key(control.CapabilityId, control.InstanceId)] = new WrittenValue(requested, readback.Value);
            _log.Info(
                "command",
                $"{Describe(control)} set to {Render(requested)}, "
                + (verified ? "verified." : $"read back {Render(readback.Value)}; the written value stands."));

            // The written value is what is published, verified or not.
            await PublishAsync(control, requested, true, null,
                verified ? HardwareStateQuality.Verified : HardwareStateQuality.Observed, true,
                cancellationToken).ConfigureAwait(false);

            return new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = verified ? CommandOutcome.AppliedVerified : CommandOutcome.AppliedUnverified,
                ReadbackValue = verified ? readback.Value : null,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            _lane.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(
        ApplicationProfileSync sync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sync);
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running || _model is not { } model || _synchronizer is null)
            {
                return new ApplicationProfileSyncResult(0, 0,
                [
                    new ApplicationProfileFailure("", "", "", "The Intel driver is not open.")
                ]);
            }

            try
            {
                return _synchronizer.Apply(sync, model.FindTarget, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                _log.Error("profiles", $"The per-application sync failed: {error.Message}");
                return new ApplicationProfileSyncResult(0, 0,
                [
                    new ApplicationProfileFailure("", "", "", "The per-application sync failed.")
                ]);
            }
        }
        finally
        {
            _lane.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<PluginHealth> StartAsync(
        IPluginHost host,
        PluginContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host = host;
        _context = context;
        _capabilities = host.Capabilities;
        if (_capabilities is null)
        {
            Health(PluginHealth.Unavailable, "WSGM admitted the plugin without a capability host.");
            return PluginHealth.Unavailable;
        }

        _log = new IntelLog(_capabilities);
        PluginHealth health;
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _colors = ColorStore.Load(context.StateDirectory, _log);
            _synchronizer = new ApplicationProfileSynchronizer(Registry.LocalMachine, context.StateDirectory, _log);
            _running = true;
            health = await OpenCycleGuardedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Startup unwinds what it acquired; the host decides whether to try again.
            _running = false;
            CloseSession();
            throw;
        }
        finally
        {
            _lane.Release();
        }

        StartLoop();
        return health;
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
        _context = context;
        await StopLoopAsync().ConfigureAwait(false);
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloseSession();
            _log.Info("lifecycle", "Suspended; the driver session is closed.");
        }
        finally
        {
            _lane.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        if (_capabilities is null)
        {
            return;
        }

        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running)
            {
                return;
            }

            _ = await OpenCycleGuardedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lane.Release();
        }

        StartLoop();
    }

    /// <summary>Opens a new cycle, turning a failure other than cancellation into failed health.</summary>
    private async Task<PluginHealth> OpenCycleGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await OpenCycleAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            _log.Error("lifecycle", $"Opening the cycle failed: {error.Message}");
            CloseSession();
            return Health(PluginHealth.Failed, "Opening the Intel driver failed.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        var loopStopped = await StopLoopAsync().ConfigureAwait(false);
        try
        {
            await _lane.WaitAsync(StopBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            _running = false;
            CloseSession();
            _api?.Dispose();
            _api = null;
            _log.Info("lifecycle", "Stopped.");
            return loopStopped;
        }
        finally
        {
            _lane.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_context is { } context)
        {
            _ = await StopAsync(context, CancellationToken.None).ConfigureAwait(false);
        }

        _disposed = true;
        _host = null;
        _capabilities = null;
    }

    /// <summary>Opens a session and publishes what it offers. Runs on the lane.</summary>
    /// <param name="newCycle">Whether this is a new capability cycle (start or resume).</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>The resulting health.</returns>
    private async Task<PluginHealth> OpenCycleAsync(bool newCycle, CancellationToken cancellationToken)
    {
        CloseSession();
        if (newCycle)
        {
            _cycleGeneration = _capabilities!.CycleGeneration;
            _descriptorFingerprint = null;
            _written.Clear();
            _published.Clear();
        }

        _api ??= IgclApi.TryLoad(_log);
        if (_api is null)
        {
            await RetractAsync(cancellationToken).ConfigureAwait(false);
            return Health(PluginHealth.Unavailable, "The Intel graphics driver's control library is not installed.");
        }

        _session = Guard(() => IgclSession.TryOpen(_api, _log));
        if (_session is null)
        {
            await RetractAsync(cancellationToken).ConfigureAwait(false);
            return Health(PluginHealth.Unavailable, "No Intel graphics adapter answered.");
        }

        _log.Info(
            "lifecycle",
            $"IGCL 0x{_session.SupportedVersion:x} open: {_session.Adapters.Count} adapter(s), "
            + $"{_session.Outputs.Count} active display(s).");
        IntelGraphicsMemoryTransport memory = new(_log);
        _model = Guard(() => IntelModel.Build(_session, memory, _colors!, _log));
        if (_model is null)
        {
            await RetractAsync(cancellationToken).ConfigureAwait(false);
            return Health(PluginHealth.Failed, "Reading the driver's capabilities failed.");
        }

        await PublishDescriptorsAsync(_model, cancellationToken).ConfigureAwait(false);
        await ObserveAsync(true, cancellationToken).ConfigureAwait(false);
        return Health(PluginHealth.Ready, $"{_model.Controls.Count} controls published.");
    }

    private async Task PublishDescriptorsAsync(IntelModel model, CancellationToken cancellationToken)
    {
        var fingerprint = JsonSerializer.Serialize(new
        {
            model.Sections,
            Descriptors = model.Controls.Select(control => control.Descriptor)
        });
        if (fingerprint == _descriptorFingerprint)
        {
            return;
        }

        _descriptorFingerprint = fingerprint;
        _published.Clear();
        _descriptorGeneration++;
        await _capabilities!.PublishDescriptorsAsync(
            new CapabilityDescriptorSet
            {
                Generation = _descriptorGeneration,
                CycleGeneration = _cycleGeneration,
                Sections = model.Sections,
                Descriptors = [.. model.Controls.Select(control => control.Descriptor)]
            },
            cancellationToken).ConfigureAwait(false);
        _log.Info(
            "lifecycle",
            $"Published descriptor generation {_descriptorGeneration}: {model.Controls.Count} controls in "
            + $"{model.Sections.Count} sections.");
    }

    /// <summary>Publishes an empty set so nothing lingers from a session that is gone.</summary>
    private async Task RetractAsync(CancellationToken cancellationToken)
    {
        _model = null;
        if (_descriptorFingerprint == string.Empty || _capabilities is null)
        {
            return;
        }

        _descriptorFingerprint = string.Empty;
        _published.Clear();
        _descriptorGeneration++;
        await _capabilities.PublishDescriptorsAsync(
            new CapabilityDescriptorSet { Generation = _descriptorGeneration, CycleGeneration = _cycleGeneration },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads every control and publishes what changed. Runs on the lane.</summary>
    private async Task ObserveAsync(bool force, CancellationToken cancellationToken)
    {
        if (_model is not { } model)
        {
            return;
        }

        foreach (var control in model.Controls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Guard(control.Read, control);
            var key = IntelModel.Key(control.CapabilityId, control.InstanceId);
            var value = read.Value;
            var quality = HardwareStateQuality.Observed;
            if (_written.TryGetValue(key, out var written))
            {
                // The written value stands for the rest of the cycle, until the driver reports a value
                // other than the one it reported right after the write: then someone else changed it.
                if (IntelControl.ValueEquals(value, written.ReadbackAtWrite) || value is null)
                {
                    value = written.Value;
                }
                else
                {
                    _written.Remove(key);
                }
            }

            if (read.Value is null && read.Result != IgclResult.Success)
            {
                _log.Change(DeviceTraceLevel.Warn, "observe", key,
                    $"{Describe(control)} could not be read ({IgclResult.Describe(read.Result)}).");
            }
            else
            {
                _log.Change(DeviceTraceLevel.Info, "observe", key, $"{Describe(control)} is {Render(value)}.");
            }

            await PublishAsync(control, value, read.Available, read.Reason,
                value is null ? HardwareStateQuality.Unknown : quality, force, cancellationToken).ConfigureAwait(false);
            if (_session?.Lost == true)
            {
                return;
            }
        }
    }

    private async Task PublishAsync(
        IntelControl control,
        CapabilityValue? value,
        bool available,
        CapabilityReason? reason,
        HardwareStateQuality quality,
        bool force,
        CancellationToken cancellationToken)
    {
        var key = IntelModel.Key(control.CapabilityId, control.InstanceId);
        var now = DateTimeOffset.UtcNow;
        if (!force
            && _published.TryGetValue(key, out var last)
            && IntelControl.ValueEquals(last.Value, value)
            && last.Available == available
            && last.Quality == quality
            && now - last.At < RefreshInterval)
        {
            return;
        }

        _published[key] = new PublishedState(value, available, quality, now);
        try
        {
            await _capabilities!.PublishCapabilityStateAsync(
                new CapabilityState
                {
                    CapabilityId = control.CapabilityId,
                    InstanceId = control.InstanceId,
                    Available = available,
                    Reason = reason,
                    ObservedValue = value,
                    Quality = quality,
                    ObservedAt = now,
                    DescriptorGeneration = _descriptorGeneration,
                    CycleGeneration = _cycleGeneration
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            _published.Remove(key);
            _log.Change(DeviceTraceLevel.Warn, "publish", key, $"WSGM refused the state of {Describe(control)}: {error.Message}");
        }
    }

    private void StartLoop()
    {
        if (!_running || _loopTask is not null)
        {
            return;
        }

        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
    }

    private async Task<bool> StopLoopAsync()
    {
        if (_loopTask is not { } task)
        {
            return true;
        }

        await _loop!.CancelAsync().ConfigureAwait(false);
        var finished = await Task.WhenAny(task, Task.Delay(StopBudget)).ConfigureAwait(false) == task;
        if (!finished)
        {
            _log.Warn("lifecycle", "The observation loop did not stop within its budget.");
        }

        _loop.Dispose();
        _loop = null;
        _loopTask = null;
        return finished;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ObservationInterval, cancellationToken).ConfigureAwait(false);
                await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!_running)
                    {
                        return;
                    }

                    if (_session is null || _session.Lost)
                    {
                        // A driver update or reset: reopen within the same cycle and republish only if
                        // what the driver offers changed.
                        _ = await OpenCycleAsync(false, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ObserveAsync(false, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _lane.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // One failed pass never ends observation.
                _log.Error("observe", $"An observation pass failed: {error.Message}");
            }
        }
    }

    private void CloseSession()
    {
        _model = null;
        if (_session is null)
        {
            return;
        }

        _session.Dispose();
        _session = null;
    }

    private PluginHealth Health(PluginHealth health, string detail)
    {
        _log.Info("lifecycle", $"{health}: {detail}");
        if (_host is { } host && _context is { } context)
        {
            try
            {
                host.PublishHealth(new PluginHealthPublication(context.Instance, context.Generation, health, detail));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _log.Warn("lifecycle", $"Health publication failed: {error.Message}");
            }
        }

        return health;
    }

    private ControlRead Guard(Func<ControlRead> read, IntelControl control)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.Change(DeviceTraceLevel.Error, "observe", IntelModel.Key(control.CapabilityId, control.InstanceId),
                $"{Describe(control)} threw on read: {error.Message}");
            return ControlRead.Failed(IgclResult.DataNotFound);
        }
    }

    private ControlWrite Guard(Func<ControlWrite> write, IntelControl control)
    {
        try
        {
            return write();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new ControlWrite(WriteStatus.Uncertain, IgclResult.DataNotFound,
                $"{Describe(control)} threw during the write: {error.Message}");
        }
    }

    private T? Guard<T>(Func<T?> action)
        where T : class
    {
        try
        {
            return action();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.Error("lifecycle", $"Opening the driver failed: {error.Message}");
            return null;
        }
    }

    private static string Describe(IntelControl control)
    {
        return $"{control.CapabilityId}[{control.InstanceId}]";
    }

    private static string Render(CapabilityValue? value)
    {
        return value switch
        {
            null => "unknown",
            { BooleanValue: { } boolean } => boolean ? "on" : "off",
            { IntegerValue: { } integer } => integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            { ChoiceValue: { } choice } => choice,
            _ => value.Kind.ToString()
        };
    }

    private static CapabilityCommandResult Rejected(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail)
    {
        return Rejected(command, new CapabilityReason(code, detail));
    }

    private static CapabilityCommandResult Rejected(CapabilityCommand command, CapabilityReason reason)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = reason,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A value WSGM wrote, and what the driver reported right after.</summary>
    private sealed record WrittenValue(CapabilityValue Value, CapabilityValue? ReadbackAtWrite);

    /// <summary>The last state published for one control.</summary>
    private sealed record PublishedState(
        CapabilityValue? Value,
        bool Available,
        HardwareStateQuality Quality,
        DateTimeOffset At);
}
