using System.Globalization;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Gpu;
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
///         and stop. Every IGCL call runs on one lane, so reads never race writes, and off the caller's
///         thread, so a command from the overlay never holds its UI thread for a driver call.
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

    /// <summary>The longest wait between attempts to open a driver that is missing or refused.</summary>
    private static readonly TimeSpan MaxReopenDelay = TimeSpan.FromMinutes(5);

    private readonly Func<IntelLog, IntelGraphicsMemoryTransport> _createMemory;

    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly Func<IntelLog, IgclApi?> _loadApi;
    private readonly Dictionary<string, PublishedState> _published = new(StringComparer.Ordinal);
    private readonly IRegistryNode _registry;
    private readonly Dictionary<string, ControlWrite> _supportResults = new(StringComparer.Ordinal);
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
    private bool _supportReadPending;
    private ApplicationProfileSynchronizer? _synchronizer;

    /// <summary>Uses the installed driver's exports and the machine registry.</summary>
    public IntelGpuPlugin()
        : this(IgclApi.TryLoad, WindowsRegistryNode.LocalMachine, log => new IntelGraphicsMemoryTransport(log))
    {
    }

    internal IntelGpuPlugin(Func<IntelLog, IgclApi?> loadApi, IRegistryNode registry,
        Func<IntelLog, IntelGraphicsMemoryTransport> createMemory)
    {
        _loadApi = loadApi;
        _registry = registry;
        _createMemory = createMemory;
    }

    /// <inheritdoc />
    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
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
            if (!_running || _model is null || _capabilities is null)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.HostUnavailable,
                    "The Intel driver is not open.");
            }

            if (command.ExpectedCycleGeneration != _cycleGeneration
                || command.ExpectedDescriptorGeneration != _descriptorGeneration)
            {
                return CommandResults.Rejected(command, new CapabilityReason(CapabilityReasonCode.GenerationChanged,
                    "The command was authored against an earlier descriptor set.", true));
            }

            if (command.Deadline.HasExpired)
            {
                return CommandResults.Rejected(command, new CapabilityReason(CapabilityReasonCode.Quiescing,
                    "Command deadline passed before it could be applied.", true));
            }

            if (_model.Find(command.CapabilityId, command.InstanceId) is not { } control)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                    $"{command.CapabilityId} is not published for {command.InstanceId}.");
            }

            if (!control.Validate(command.RequestedValue, out var error))
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                    error ?? "The value is not accepted.");
            }

            var requested = command.RequestedValue!;
            var admission = new WriteAdmission(cancellationToken, command.Deadline,
                () => _running && _session is not null);

            // Admission may come from the overlay's UI thread; the driver work runs off it.
            if (await Task.Run(() => WriteAndRead(control, requested, admission), CancellationToken.None)
                    .ConfigureAwait(false) is not { } outcome)
            {
                return CommandResults.Rejected(command, new CapabilityReason(CapabilityReasonCode.Quiescing,
                    "The command was cancelled or expired before its driver write.", true));
            }

            var (write, readback) = outcome;
            switch (write.Status)
            {
                case WriteStatus.Refused:
                    _log.Warn("command", $"{control.Name} refused: {write.Detail}");
                    return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                        write.Detail ?? "The driver refused.");
                case WriteStatus.Uncertain:
                    // Never retried: whether anything changed is unknown, and a second write on top of an
                    // unknown state is a guess. The next observation reports what the driver holds.
                    _log.Error("command", $"{control.Name} failed: {write.Detail}");
                    return CommandResults.Indeterminate(
                        command,
                        CapabilityReasonCode.TransportFaulted,
                        write.Detail ?? "The driver call failed.",
                        RollbackResult.NotRequired);
            }

            var verified = readback.Value is not null && readback.Value == requested;
            _written[control.Key] = new WrittenValue(requested, readback.Value);
            _log.Info(
                "command",
                $"{control.Name} set to {Render(requested)}, "
                + (verified ? "verified." : $"read back {Render(readback.Value)}; the written value stands."));

            // The written value is what is published, verified or not.
            await PublishAsync(control, requested, true, null,
                verified ? HardwareStateQuality.Verified : HardwareStateQuality.Observed, true,
                CancellationToken.None).ConfigureAwait(false);

            return verified
                ? CommandResults.Verified(command, readback.Value!)
                : CommandResults.Unverified(command);
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
            if (!_running || _model is not { } model || _synchronizer is not { } synchronizer)
            {
                return new ApplicationProfileSyncResult(0, 0,
                [
                    new ApplicationProfileFailure("", "", "", "The Intel driver is not open.")
                ]);
            }

            try
            {
                var session = _session;
                var admission = new WriteAdmission(cancellationToken, Deadline.Never,
                    () => _running && _session == session);
                return await Task.Run(() =>
                {
                    session?.BeginPass();
                    return synchronizer.Apply(sync, model.FindTarget, cancellationToken, admission);
                }, CancellationToken.None).ConfigureAwait(false);
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
    public string Id => "wsgm.gpu.intel";

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
        _log = new IntelLog(host);
        _capabilities = host.Capabilities;
        if (_capabilities is null)
        {
            return Health(PluginHealth.Unavailable, "WSGM admitted the plugin without a capability host.");
        }

        PluginHealth health;
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _closePending, 0);
            _colors = ColorStore.Load(context.StateDirectory, _log);
            _synchronizer = new ApplicationProfileSynchronizer(_registry, context.StateDirectory,
                _log);

            // Resolved once for the life of the plugin; a session reopen does not look for it again.
            _memory = _createMemory(_log);
            _running = true;
            ResetReopen();

            // Opening probes setters, so it runs off the caller's thread like every other driver call.
            health = await Task.Run(() => OpenCycleGuardedAsync(true, cancellationToken, context.Deadline),
                    CancellationToken.None)
                .ConfigureAwait(false);
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
        await StopLoopAsync(cancellationToken).ConfigureAwait(false);
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        var closing = Task.Run(() =>
        {
            try
            {
                CloseSession();
                _log.Info("lifecycle", "Suspended; the driver session is closed.");
            }
            finally
            {
                ReleaseLane();
            }
        }, CancellationToken.None);
        await closing.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _ = await Task.Run(() => OpenCycleGuardedAsync(true, cancellationToken, context.Deadline),
                    CancellationToken.None)
                .ConfigureAwait(false);
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
    ///     Bounded by the caller's token. When another thread is still inside a driver call after that,
    ///     the session and <c>ControlLib.dll</c> are not pulled out from under it; stop reports itself
    ///     unconfirmed and the thread holding the lane closes them when its call returns.
    /// </remarks>
    public async ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _running = false;
        var loopStopped = await StopLoopAsync(cancellationToken).ConfigureAwait(false);
        bool acquired;
        try
        {
            await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
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

        // Closing is a driver call too. The task owns the lane until it finishes, even when the caller's
        // budget ends first; neither a resume nor another stop can unload or reopen under ctlClose.
        var closing = Task.Run(() =>
        {
            try
            {
                CloseDriver();
                _log.Info("lifecycle", "Stopped.");
            }
            finally
            {
                ReleaseLane();
            }
        }, CancellationToken.None);
        try
        {
            await closing.WaitAsync(cancellationToken).ConfigureAwait(false);
            return loopStopped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Warn("lifecycle", "The driver is still closing; cleanup keeps the native lane until it returns.");
            return false;
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
            // Dispose has no budget of its own: it closes what is free now, and a running driver call closes
            // the rest when it returns.
            _ = await StopAsync(context, new CancellationToken(true)).ConfigureAwait(false);
        }

        _host = null;
        _capabilities = null;
    }

    /// <summary>Opens a new cycle, turning a failure other than cancellation into failed health.</summary>
    private async Task<PluginHealth> OpenCycleGuardedAsync(bool newCycle, CancellationToken cancellationToken,
        Deadline deadline)
    {
        try
        {
            return await OpenCycleAsync(newCycle, cancellationToken, deadline).ConfigureAwait(false);
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
    /// <param name="deadline">This lifecycle call's active-time limit, or no limit for observation.</param>
    /// <returns>The resulting health.</returns>
    /// <remarks>The loop's first pass, right after, publishes every state.</remarks>
    private async Task<PluginHealth> OpenCycleAsync(bool newCycle, CancellationToken cancellationToken,
        Deadline deadline)
    {
        CloseSession();
        _published.Clear();
        if (newCycle)
        {
            _cycleGeneration = _capabilities!.CycleGeneration;
            _descriptorFingerprint = null;
            _written.Clear();
        }

        _api ??= _loadApi(_log);
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
        _adapterKeys = AdapterClassKey.Enumerate(_registry, AdapterClassKey.ClassPath, _log);
        return await BuildAsync(_session, cancellationToken, deadline).ConfigureAwait(false);
    }

    /// <summary>Builds the model for the open session and publishes its descriptors. Runs on the lane.</summary>
    private async Task<PluginHealth> BuildAsync(IgclSession session, CancellationToken cancellationToken,
        Deadline deadline)
    {
        _model = Guard(() => IntelModel.Build(session, _memory!, _colors!, _adapterKeys, _log));
        if (_model is null)
        {
            await RetractAsync(cancellationToken).ConfigureAwait(false);
            return Health(PluginHealth.Failed, "Reading the driver's capabilities failed.");
        }

        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        var admission = new WriteAdmission(cancellationToken, deadline, () => _running && _session == session);
        _supportReadPending = false;
        foreach (var control in _model.Controls)
        {
            var read = GuardRead(control);
            RecordUnsupported(control, read.Failure, unsupported);
            if (read.Failure is not null && !unsupported.Contains(control.Key)
                                         && control.Descriptor.SupportsWrite &&
                                         !_supportResults.ContainsKey(control.SupportKey))
            {
                // A failed read is not a setter-support outcome. Retry only this read on a later pass;
                // accepted or failed setter probes remain cached and are never repeated.
                unsupported.Add(control.Key);
                _supportReadPending = true;
                _log.Change(DeviceTraceLevel.Warn, "support", control.SupportKey,
                    $"Support read failed; {control.Name} skipped this pass: {read.Failure}");
            }
        }

        foreach (var control in _model.Controls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!control.Descriptor.SupportsWrite || unsupported.Contains(control.Key))
            {
                continue;
            }

            if (!_supportResults.TryGetValue(control.SupportKey, out var support))
            {
                try
                {
                    support = control.ProbeSupport(admission);
                }
                catch (DriverFailure error) when (!error.Attempted && !admission.Admitted)
                {
                    throw new OperationCanceledException(error.Message, error, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    support = new ControlWrite(WriteStatus.Uncertain,
                        new ControlFailure(FailureKind.Exception, Detail: IntelLog.Describe(error)),
                        IntelLog.Describe(error));
                }

                // One outcome per structure. Failed and uncertain probes are never automatically retried
                // during this plugin lifetime.
                _supportResults[control.SupportKey] = support;
                _log.Info("support",
                    $"{control.Name}: {support.Status}; {support.Detail ?? "native state returned unchanged"}.");
            }

            if (support.Status != WriteStatus.Applied)
            {
                unsupported.Add(control.Key);
            }

            if (session.Lost)
            {
                return Health(PluginHealth.Unavailable, "The Intel driver was lost during support discovery.");
            }
        }

        _model = _model.WithoutControls(unsupported);
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
        try
        {
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
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            _log.Change(DeviceTraceLevel.Warn, "publish", "descriptors",
                "WSGM refused the descriptor set: " + IntelLog.Describe(error));
        }
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
        try
        {
            await _capabilities.PublishDescriptorsAsync(
                new CapabilityDescriptorSet { Generation = _descriptorGeneration, CycleGeneration = _cycleGeneration },
                cancellationToken).ConfigureAwait(false);
            _descriptorFingerprint = string.Empty;
            _published.Clear();
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            _log.Change(DeviceTraceLevel.Warn, "publish", "descriptors",
                "WSGM refused the empty descriptor set: " + IntelLog.Describe(error));
        }
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
            _ = await OpenCycleGuardedAsync(false, cancellationToken, Deadline.Never).ConfigureAwait(false);
            ScheduleReopen();
            if (_session is null)
            {
                return;
            }
        }
        else if (_session.RefreshOutputs() || _supportReadPending)
        {
            _log.Change(DeviceTraceLevel.Info, "lifecycle", "rebuild",
                _supportReadPending
                    ? "A support read is still pending; discovering the controls again."
                    : $"The active displays changed: {_session.Outputs.Count} now; rebuilding.");
            _ = await BuildAsync(_session, cancellationToken, Deadline.Never).ConfigureAwait(false);
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

    /// <summary>Filters explicit unsupported results during discovery, before any UI descriptors are published.</summary>
    private void RecordUnsupported(IntelControl control, ControlFailure? failure, HashSet<string> unsupported)
    {
        if (failure is not { Kind: FailureKind.DriverResult } result
            || !IgclResult.IsUnsupportedFeature(result.Result))
        {
            return;
        }

        if (unsupported.Add(control.Key))
        {
            _log.Info("capabilities", $"{control.Name} is unsupported ({IgclResult.Describe(result.Result)}); hidden.");
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
        if (!_running || _loop is not null)
        {
            return;
        }

        var loop = new CancellationTokenSource();
        _loop = loop;
        var token = loop.Token;

        // A stopped loop still in its pass finishes first, so two passes never overlap.
        var previous = _loopTask;
        _loopTask = Task.Run(async () =>
        {
            try
            {
                if (previous is not null)
                {
                    await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                await RunLoopAsync(token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _loop, null, loop);
                loop.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>Cancels the loop and waits for it within the caller's budget.</summary>
    /// <param name="cancellationToken">The caller's budget.</param>
    /// <returns><see langword="true" /> when the loop finished.</returns>
    /// <remarks>A loop still in its pass stays recorded, so the next start runs after it.</remarks>
    private async Task<bool> StopLoopAsync(CancellationToken cancellationToken)
    {
        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            try
            {
                await loop.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The loop finished and disposed its own cancellation source after we took the reference.
            }
        }

        if (_loopTask is { } task)
        {
            try
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _log.Warn("lifecycle", "The observation loop is still in a pass; it ends when the pass does.");
                return false;
            }

            _loopTask = null;
        }

        return true;
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

    /// <summary>One command's driver work, off the caller's thread: a last admission check, write, readback.</summary>
    /// <returns>The write and its readback, or null when the command was no longer admitted.</returns>
    private (ControlWrite Write, ControlRead Readback)? WriteAndRead(
        IntelControl control,
        CapabilityValue value,
        WriteAdmission admission)
    {
        // A command is its own pass: the fields a write carries are read afresh, and so is the readback.
        _session?.BeginPass();
        if (!admission.Admitted)
        {
            return null;
        }

        try
        {
            var write = GuardWrite(control, value, admission);
            return (write, write.Status == WriteStatus.Applied ? GuardRead(control) : default);
        }
        catch (DriverFailure error) when (!error.Attempted && !admission.Admitted)
        {
            return null;
        }
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
    private static ControlWrite GuardWrite(IntelControl control, CapabilityValue value, WriteAdmission admission)
    {
        try
        {
            return control.Write(value, admission);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not DriverFailure { Attempted: false })
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
            { IntegerValue: { } integer } => integer.ToString(CultureInfo.InvariantCulture),
            { ChoiceValue: { } choice } => choice,
            _ => value.Kind.ToString()
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
