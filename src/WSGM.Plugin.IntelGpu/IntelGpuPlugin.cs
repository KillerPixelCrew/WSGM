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

    /// <summary>How long stop waits for the observation loop, and then for the lane.</summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between attempts to open a driver that is missing or refused.</summary>
    private static readonly TimeSpan MaxReopenDelay = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly Dictionary<string, PublishedState> _published = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WrittenValue> _written = new(StringComparer.Ordinal);
    private IReadOnlyList<AdapterClassEntry> _adapterKeys = [];
    private IgclApi? _api;
    private ICapabilityHost? _capabilities;

    /// <summary>Set when stop could not take the lane; whoever holds it closes the driver on release.</summary>
    private int _closePending;

    private ColorStore? _colors;
    private PluginContext? _context;
    private long _cycleGeneration;
    private string? _descriptorFingerprint;
    private long _descriptorGeneration;
    private int _disposed;
    private (long Generation, PluginHealth Health, string Detail)? _health;
    private IPluginHost? _host;
    private IntelLog _log = IntelLog.None;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private IntelGraphicsMemoryTransport? _memory;
    private IntelModel? _model;
    private long _reopenAt;
    private TimeSpan _reopenDelay = ObservationInterval;
    private volatile bool _running;
    private IgclSession? _session;
    private ApplicationProfileSynchronizer? _synchronizer;

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

            // A command is its own pass: the fields a write carries are read afresh, and so is the readback.
            _session?.BeginPass();
            var requested = command.RequestedValue!;
            var write = GuardWrite(control, requested);
            switch (write.Status)
            {
                case WriteStatus.Refused:
                    _log.Warn("command", $"{control.Name} refused: {write.Detail}");
                    return Rejected(command, CapabilityReasonCode.Unsupported, write.Detail ?? "The driver refused.");
                case WriteStatus.Uncertain:
                    // Never retried: whether anything changed is unknown, and a second write on top of an
                    // unknown state is a guess. The next observation reports what the driver holds.
                    _log.Error("command", $"{control.Name} failed: {write.Detail}");
                    return new CapabilityCommandResult
                    {
                        CommandId = command.CommandId,
                        Outcome = CommandOutcome.Indeterminate,
                        Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted, write.Detail),
                        CompletedAt = DateTimeOffset.UtcNow
                    };
            }

            var readback = GuardRead(control);
            var verified = readback.Value is not null && readback.Value == requested;
            _written[control.Key] = new WrittenValue(requested, readback.Value);
            _log.Info(
                "command",
                $"{control.Name} set to {Render(requested)}, "
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
            ReleaseLane();
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
                _session?.BeginPass();
                return _synchronizer.Apply(sync, model.FindTarget, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                _log.Failure("profiles", "The per-application sync failed", error);
                return new ApplicationProfileSyncResult(0, 0,
                [
                    new ApplicationProfileFailure("", "", "", "The per-application sync failed.")
                ]);
            }
        }
        finally
        {
            ReleaseLane();
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _host = host;
        _context = context;
        _capabilities = host.Capabilities;
        if (_capabilities is null)
        {
            return Health(PluginHealth.Unavailable, "WSGM admitted the plugin without a capability host.");
        }

        _log = new IntelLog(_capabilities);
        PluginHealth health;
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _closePending, 0);
            _colors = ColorStore.Load(context.StateDirectory, _log);
            _synchronizer = new ApplicationProfileSynchronizer(Registry.LocalMachine, context.StateDirectory, _log);

            // Resolved once for the life of the plugin; a session reopen does not look for it again.
            _memory = new IntelGraphicsMemoryTransport(_log);
            _running = true;
            ResetReopen();
            health = await OpenCycleGuardedAsync(true, cancellationToken).ConfigureAwait(false);
            ScheduleReopen();
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
            ReleaseLane();
        }

        // The loop runs the first observation straight away, so start returns once descriptors are out.
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
            ReleaseLane();
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

            ResetReopen();
            _ = await OpenCycleGuardedAsync(true, cancellationToken).ConfigureAwait(false);
            ScheduleReopen();
        }
        finally
        {
            ReleaseLane();
        }

        StartLoop();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Bounded: the loop and the lane each get <see cref="StopBudget" />. When another thread is still
    ///     inside a driver call after that, the session and <c>ControlLib.dll</c> are not pulled out from
    ///     under it; stop reports itself unconfirmed and the thread holding the lane closes them when its
    ///     call returns.
    /// </remarks>
    public async ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _running = false;
        var loopStopped = await StopLoopAsync().ConfigureAwait(false);
        bool acquired;
        try
        {
            acquired = await _lane.WaitAsync(StopBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            acquired = false;
        }

        if (!acquired)
        {
            Interlocked.Exchange(ref _closePending, 1);

            // The holder may have released between the timeout and the flag; then the lane is free now.
            if (!_lane.Wait(0))
            {
                _log.Warn("lifecycle", "A driver call is still running; the driver closes when it returns.");
                return false;
            }
        }

        try
        {
            CloseDriver();
            _log.Info("lifecycle", "Stopped.");
            return loopStopped;
        }
        finally
        {
            ReleaseLane();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_context is { } context)
        {
            _ = await StopAsync(context, CancellationToken.None).ConfigureAwait(false);
        }

        _host = null;
        _capabilities = null;
    }

    /// <summary>Opens a new cycle, turning a failure other than cancellation into failed health.</summary>
    private async Task<PluginHealth> OpenCycleGuardedAsync(bool newCycle, CancellationToken cancellationToken)
    {
        try
        {
            return await OpenCycleAsync(newCycle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            _log.Change(DeviceTraceLevel.Error, "lifecycle", "open",
                $"Opening the cycle failed: {IntelLog.Describe(error)}");
            CloseSession();
            return Health(PluginHealth.Failed, "Opening the Intel driver failed.");
        }
    }

    /// <summary>Opens a session and publishes what it offers. Runs on the lane.</summary>
    /// <param name="newCycle">Whether this is a new capability cycle (start or resume).</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>The resulting health.</returns>
    /// <remarks>The loop's first pass, right after, publishes every state.</remarks>
    private async Task<PluginHealth> OpenCycleAsync(bool newCycle, CancellationToken cancellationToken)
    {
        CloseSession();
        _published.Clear();
        if (newCycle)
        {
            _cycleGeneration = _capabilities!.CycleGeneration;
            _descriptorFingerprint = null;
            _written.Clear();
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
        _adapterKeys = AdapterClassKey.Enumerate(Registry.LocalMachine, AdapterClassKey.ClassPath, _log);
        return await BuildAsync(_session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the model for the open session and publishes its descriptors. Runs on the lane.</summary>
    private async Task<PluginHealth> BuildAsync(IgclSession session, CancellationToken cancellationToken)
    {
        _model = Guard(() => IntelModel.Build(session, _memory!, _colors!, _adapterKeys, _log));
        if (_model is null)
        {
            await RetractAsync(cancellationToken).ConfigureAwait(false);
            return Health(PluginHealth.Failed, "Reading the driver's capabilities failed.");
        }

        await PublishDescriptorsAsync(_model, cancellationToken).ConfigureAwait(false);
        return Health(PluginHealth.Ready, $"{_model.Controls.Count} controls published.");
    }

    private async Task PublishDescriptorsAsync(IntelModel model, CancellationToken cancellationToken)
    {
        if (model.Fingerprint == _descriptorFingerprint)
        {
            return;
        }

        // The generation only ever grows, even when the host refuses a set, so a later set never
        // reuses a number the host may already have seen.
        _descriptorGeneration++;
        await _capabilities!.PublishDescriptorsAsync(
            new CapabilityDescriptorSet
            {
                Generation = _descriptorGeneration,
                CycleGeneration = _cycleGeneration,
                Sections = model.Sections,
                Descriptors = model.PublishedDescriptors
            },
            cancellationToken).ConfigureAwait(false);

        // Recorded only once WSGM took the set, so a refused set is published again on the next pass.
        _descriptorFingerprint = model.Fingerprint;
        _published.Clear();
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

        _descriptorGeneration++;
        await _capabilities.PublishDescriptorsAsync(
            new CapabilityDescriptorSet { Generation = _descriptorGeneration, CycleGeneration = _cycleGeneration },
            cancellationToken).ConfigureAwait(false);
        _descriptorFingerprint = string.Empty;
        _published.Clear();
    }

    /// <summary>One loop pass: reopen a lost or missing driver, follow display changes, observe. Runs on the lane.</summary>
    private async Task PassAsync(CancellationToken cancellationToken)
    {
        if (_session is null || _session.Lost)
        {
            if (Environment.TickCount64 < _reopenAt)
            {
                return;
            }

            // A driver update or reset: reopen within the same cycle and republish only if what the
            // driver offers changed. A missing or refusing driver is asked again less and less often.
            _ = await OpenCycleGuardedAsync(false, cancellationToken).ConfigureAwait(false);
            ScheduleReopen();
            if (_session is null)
            {
                return;
            }
        }
        else if (_session.RefreshOutputs())
        {
            _log.Info("lifecycle", $"The active displays changed: {_session.Outputs.Count} now; rebuilding.");
            _ = await BuildAsync(_session, cancellationToken).ConfigureAwait(false);
        }
        else if (_model is { } model && model.Fingerprint != _descriptorFingerprint)
        {
            await PublishDescriptorsAsync(model, cancellationToken).ConfigureAwait(false);
        }

        await ObserveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads every control and publishes what changed. Runs on the lane.</summary>
    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        if (_model is not { } model || _session is not { } session || session.Lost)
        {
            return;
        }

        session.BeginPass();
        foreach (var control in model.Controls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = GuardRead(control);
            var value = read.Value;
            if (_written.TryGetValue(control.Key, out var written))
            {
                // The written value stands for the rest of the cycle, until the driver reports a value
                // other than the one it reported right after the write: then someone else changed it.
                if (value is null || value == written.ReadbackAtWrite)
                {
                    value = written.Value;
                }
                else
                {
                    _written.Remove(control.Key);
                }
            }

            if (control.TraceDue(value, read.Available, read.Failure))
            {
                if (read.Failure is { } failure)
                {
                    _log.Change(DeviceTraceLevel.Warn, "observe", control.Key,
                        $"{control.Name} could not be read: {failure}.");
                }
                else
                {
                    _log.Change(DeviceTraceLevel.Info, "observe", control.Key,
                        $"{control.Name} is {Render(value)}{(read.Available ? "" : " (unavailable)")}.");
                }
            }

            await PublishAsync(control, value, read.Available, read.Reason,
                value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed, false,
                cancellationToken).ConfigureAwait(false);
            if (session.Lost)
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
        var now = DateTimeOffset.UtcNow;
        if (!force
            && _published.TryGetValue(control.Key, out var last)
            && last.Value == value
            && last.Available == available
            && last.Quality == quality
            && now - last.At < RefreshInterval)
        {
            return;
        }

        _published[control.Key] = new PublishedState(value, available, quality, now);
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
            _published.Remove(control.Key);
            _log.Change(DeviceTraceLevel.Warn, "publish", control.Key,
                $"WSGM refused the state of {control.Name}: {IntelLog.Describe(error)}");
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
        using PeriodicTimer timer = new(ObservationInterval);
        try
        {
            // The first pass runs at once: it publishes every state after a start or a resume.
            do
            {
                await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!_running)
                    {
                        return;
                    }

                    await PassAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException
                                              && !(error is OperationCanceledException
                                                   && cancellationToken.IsCancellationRequested))
                {
                    // One failed pass never ends observation, and a repeating failure is logged once.
                    _log.Change(DeviceTraceLevel.Error, "observe", "pass",
                        $"An observation pass failed: {IntelLog.Describe(error)}");
                }
                finally
                {
                    ReleaseLane();
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    /// <summary>Releases the lane, closing the driver first when a timed-out stop left that to this holder.</summary>
    private void ReleaseLane()
    {
        _lane.Release();
        if (Volatile.Read(ref _closePending) == 0 || !_lane.Wait(0))
        {
            return;
        }

        try
        {
            if (!_running && Volatile.Read(ref _closePending) != 0)
            {
                CloseDriver();
                _log.Info("lifecycle", "Stopped after the running driver call returned.");
            }
        }
        finally
        {
            _lane.Release();
        }
    }

    /// <summary>Closes the session and frees <c>ControlLib.dll</c>. Runs on the lane.</summary>
    private void CloseDriver()
    {
        CloseSession();
        _api?.Dispose();
        _api = null;
        Interlocked.Exchange(ref _closePending, 0);
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

    private void ResetReopen()
    {
        _reopenAt = 0;
        _reopenDelay = ObservationInterval;
    }

    /// <summary>
    ///     After an open: resets the backoff when a session is open, else schedules the next attempt and
    ///     doubles the wait, up to <see cref="MaxReopenDelay" />.
    /// </summary>
    private void ScheduleReopen()
    {
        if (_session is not null)
        {
            ResetReopen();
            return;
        }

        _reopenAt = Environment.TickCount64 + (long)_reopenDelay.TotalMilliseconds;
        _reopenDelay = TimeSpan.FromTicks(Math.Min(_reopenDelay.Ticks * 2, MaxReopenDelay.Ticks));
    }

    /// <summary>Publishes health, and traces it, only when it changed.</summary>
    private PluginHealth Health(PluginHealth health, string detail)
    {
        if (_context is not { } context)
        {
            return health;
        }

        var current = (context.Generation, health, detail);
        if (_health == current)
        {
            return health;
        }

        _health = current;
        _log.Info("lifecycle", $"{health}: {detail}");
        if (_host is not { } host)
        {
            return health;
        }

        try
        {
            host.PublishHealth(new PluginHealthPublication(context.Instance, context.Generation, health, detail));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.Warn("lifecycle", $"Health publication failed: {IntelLog.Describe(error)}");
        }

        return health;
    }

    /// <summary>Reads one control, turning an exception into a failed read that names it.</summary>
    private static ControlRead GuardRead(IntelControl control)
    {
        try
        {
            return control.Read();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return ControlRead.Failed(new ControlFailure(FailureKind.Exception, Detail: IntelLog.Describe(error)));
        }
    }

    /// <summary>Writes one control, turning an exception into an uncertain write that names it.</summary>
    private static ControlWrite GuardWrite(IntelControl control, CapabilityValue value)
    {
        try
        {
            return control.Write(value);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var detail = $"{control.Name} threw during the write: {IntelLog.Describe(error)}";
            return new ControlWrite(WriteStatus.Uncertain, new ControlFailure(FailureKind.Exception, Detail: detail),
                detail);
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
            _log.Failure("lifecycle", "Opening the driver failed", error);
            return null;
        }
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
