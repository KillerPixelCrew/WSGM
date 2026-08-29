using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Truthful state of AutoTDP for the overlay, native QAM, and diagnostics.</summary>
internal enum AutoTdpState
{
    /// <summary>The user has not enabled AutoTDP.</summary>
    Off,

    /// <summary>Enabled, but a prerequisite is missing.</summary>
    Unavailable,

    /// <summary>Enabled and waiting for a foreground application to render.</summary>
    Idle,

    /// <summary>Actively controlling the power limit.</summary>
    Controlling,

    /// <summary>Suspended because the power limit was changed by hand.</summary>
    Paused,
}

/// <summary>The complete AutoTDP projection.</summary>
internal sealed record AutoTdpStatus(
    AutoTdpState State,
    int? Watts,
    double? FrametimeMs,
    double? TargetFrametimeMs,
    string? ApplicationId,
    string Detail);

/// <summary>
/// The one AutoTDP session service.
/// </summary>
/// <remarks>
/// A thin binding around <see cref="AutoTdpController"/>: it decides nothing itself, so the whole
/// control policy stays replayable from a recorded trace without a device. What lives here is the
/// plumbing the controller must not know about — which application is in front, which capability is
/// the primary power limit, and the rule that only one power write may be in flight.
/// <para>
/// Every prerequisite is optional and checked each tick. No RTSS, no plugin, no power capability, or
/// no rendering application simply means AutoTDP holds; none of them is an error, and none of them
/// may take a frame limit or a manual power setting away from the user.
/// </para>
/// </remarks>
internal sealed class AutoTdpService : IAsyncDisposable
{
    /// <summary>How often frame delivery is judged.</summary>
    /// <remarks>
    /// One second per window. Shorter windows judge a power change before the SoC has finished
    /// responding to the previous one; longer ones let a stutter run for too long before power rises.
    /// </remarks>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly IFrametimeSource _frametimes;
    private readonly Func<IReadOnlyList<DeviceCapabilityView>> _capabilities;
    private readonly Func<string, string?, CapabilityValue, CancellationToken, Task<CapabilityCommandResult>> _writeAsync;
    private readonly Func<double> _targetFrametimeMs;
    private readonly AutoTdpController _controller = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private Task _worker = Task.CompletedTask;
    private RunningApplicationTargetSnapshot? _running;
    private int? _restoreTo;
    private bool _enabled;
    private bool _disposed;

    internal AutoTdpService(
        IFrametimeSource frametimes,
        Func<IReadOnlyList<DeviceCapabilityView>> capabilities,
        Func<string, string?, CapabilityValue, CancellationToken, Task<CapabilityCommandResult>> writeAsync,
        Func<double> targetFrametimeMs)
    {
        ArgumentNullException.ThrowIfNull(frametimes);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(writeAsync);
        ArgumentNullException.ThrowIfNull(targetFrametimeMs);
        _frametimes = frametimes;
        _capabilities = capabilities;
        _writeAsync = writeAsync;
        _targetFrametimeMs = targetFrametimeMs;
    }

    /// <summary>Raised when the projection changes.</summary>
    internal event Action<AutoTdpStatus>? StatusChanged;

    /// <summary>Current projection.</summary>
    internal AutoTdpStatus Status { get; private set; } = new(
        AutoTdpState.Off,
        null,
        null,
        null,
        null,
        "AutoTDP is off.");

    /// <summary>Enables or disables automatic control.</summary>
    /// <param name="enabled">Whether AutoTDP should run.</param>
    internal void Apply(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (enabled)
            {
                _worker = Task.Run(() => RunAsync(_shutdown.Token));
                return;
            }
        }

        Observe(StopAsync(CancellationToken.None), "AutoTDP stop");
    }

    /// <summary>Records the running application whose frames are being judged.</summary>
    /// <param name="snapshot">The canonical running-application snapshot.</param>
    internal void ApplyRunningApplication(RunningApplicationTargetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _running = snapshot;
        }
    }

    /// <summary>Suspends control because the power limit was set by hand.</summary>
    /// <param name="watts">The limit that was just set.</param>
    /// <remarks>
    /// Called by whoever writes the power capability from a user action. Control does not resume by
    /// itself: the user has overridden the controller, and quietly taking the limit back would make
    /// the manual control look broken.
    /// </remarks>
    internal void NoteManualChange(int watts)
    {
        lock (_gate)
        {
            _controller.PauseForManualChange(watts);
        }

        Publish(AutoTdpState.Paused, watts, null, null, "Paused by a manual power change.");
    }

    /// <summary>Resumes automatic control after an explicit user request.</summary>
    internal void Resume()
    {
        lock (_gate)
        {
            _controller.Resume(_controller.Watts);
        }

        Publish(AutoTdpState.Idle, _controller.Watts, null, null, "AutoTDP resumed.");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            worker = _worker;
        }

        // Restoration first, while the write path still works: exiting with WSGM's probe value
        // latched would leave the user's handheld on a limit they never chose.
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        (_frametimes as IDisposable)?.Dispose();
        _write.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(Window);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("AutoTDP stopped after an unexpected failure", ex);
            Publish(AutoTdpState.Unavailable, null, null, null, ex.Message);
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!Volatile.Read(ref _enabled))
        {
            return;
        }

        if (FindPowerCapability() is not { } power)
        {
            Publish(AutoTdpState.Unavailable, null, null, null, "No primary power limit is available.");
            return;
        }

        AutoTdpLimits limits = new(
            power.Descriptor.Minimum ?? 0,
            power.Descriptor.Maximum ?? 0,
            power.Descriptor.Step ?? 0);
        if (!limits.IsUsable || power.Projection.State.ObservedValue?.IntegerValue is not { } current)
        {
            Publish(AutoTdpState.Unavailable, null, null, null, "The power limit reports no usable range.");
            return;
        }

        RunningApplicationTargetSnapshot? running;
        lock (_gate)
        {
            running = _running;
        }

        if (SelectSample(running) is not { } frametime)
        {
            Publish(AutoTdpState.Idle, current, null, null, "No application is rendering.");
            return;
        }

        double target = _targetFrametimeMs();
        string context = ContextKey(running, frametime);
        AutoTdpDecision decision;
        lock (_gate)
        {
            if (_restoreTo is null)
            {
                _restoreTo = current;
                _controller.Start(current, limits, context);
            }

            decision = _controller.Evaluate(
                new AutoTdpSample(frametime.MeanFrametimeMs, target, IsCapped(frametime, target), context),
                limits);
        }

        if (decision.RequiresWrite)
        {
            await WriteAsync(power, decision, cancellationToken).ConfigureAwait(false);
        }

        Publish(
            _controller.IsPaused ? AutoTdpState.Paused : AutoTdpState.Controlling,
            decision.Watts,
            frametime.MeanFrametimeMs,
            target,
            running?.ApplicationId,
            decision.Reason);
    }

    private async Task WriteAsync(
        DeviceCapabilityView power,
        AutoTdpDecision decision,
        CancellationToken cancellationToken)
    {
        // One power command at a time. An overlapping write would leave the controller unable to say
        // which value the hardware actually ended up with, and an uncertain hardware write is never
        // retried behind the user's back.
        if (!await _write.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Log.Warn("AutoTDP skipped a power write: an earlier write is still in flight.");
            return;
        }

        try
        {
            CapabilityCommandResult result = await _writeAsync(
                power.Descriptor.CapabilityId,
                power.Descriptor.InstanceId,
                new CapabilityValue
                {
                    Kind = CapabilityValueKind.Integer,
                    IntegerValue = decision.Watts,
                },
                cancellationToken).ConfigureAwait(false);
            Log.Info(
                $"AutoTDP {decision.Action}: {decision.Watts} W ({decision.Reason}), "
                + $"outcome={result.Outcome}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"AutoTDP power write failed: {ex.Message}");
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        int? restoreTo;
        DeviceCapabilityView? power = FindPowerCapability();
        lock (_gate)
        {
            restoreTo = _restoreTo;
            _restoreTo = null;
        }

        if (restoreTo is not { } watts || power is null)
        {
            Publish(AutoTdpState.Off, null, null, null, "AutoTDP is off.");
            return;
        }

        AutoTdpDecision decision = _controller.Stop(watts);
        await WriteAsync(power, decision, cancellationToken).ConfigureAwait(false);
        Publish(AutoTdpState.Off, watts, null, null, "AutoTDP is off; the previous limit was restored.");
    }

    private DeviceCapabilityView? FindPowerCapability() => _capabilities()
        .FirstOrDefault(view =>
            view.Descriptor.Role is CapabilityRole.PowerSustainedLimit
            && view.Descriptor.SupportsWrite
            && view.Descriptor.ValueKind is CapabilityValueKind.Integer);

    private RtssFrametimeSample? SelectSample(RunningApplicationTargetSnapshot? running)
    {
        IReadOnlyList<RtssFrametimeSample> live = _frametimes.ReadLive();
        if (live.Count == 0)
        {
            return null;
        }

        // The running-application monitor knows which executable Steam launched; RTSS knows which
        // process is drawing. Matching them is what keeps AutoTDP from tuning power for a launcher
        // or a background renderer that happens to be in the table.
        if (running?.ExecutablePath is { Length: > 0 } executable)
        {
            string leaf = Path.GetFileName(executable);
            foreach (RtssFrametimeSample sample in live)
            {
                if (string.Equals(
                    Path.GetFileName(sample.ExecutablePath),
                    leaf,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return sample;
                }
            }
        }

        // With exactly one renderer there is nothing to confuse it with. With several and no
        // identity, AutoTDP declines rather than guessing which one the user is playing.
        return live.Count == 1 ? live[0] : null;
    }

    private static bool IsCapped(RtssFrametimeSample sample, double targetFrametimeMs) =>
        sample.MeanFrametimeMs >= targetFrametimeMs * 0.97
        && sample.MeanFrametimeMs <= targetFrametimeMs * AutoTdpController.MissRatio;

    private static string ContextKey(
        RunningApplicationTargetSnapshot? running,
        RtssFrametimeSample sample) =>
        running?.ApplicationId is { Length: > 0 } identity
            ? identity
            : $"process:{Path.GetFileName(sample.ExecutablePath)}";

    private void Publish(
        AutoTdpState state,
        int? watts,
        double? frametimeMs,
        double? targetFrametimeMs,
        string detail) =>
        Publish(state, watts, frametimeMs, targetFrametimeMs, Status.ApplicationId, detail);

    private void Publish(
        AutoTdpState state,
        int? watts,
        double? frametimeMs,
        double? targetFrametimeMs,
        string? applicationId,
        string detail)
    {
        AutoTdpStatus status = new(
            state,
            watts,
            frametimeMs,
            targetFrametimeMs,
            applicationId,
            detail);
        if (status == Status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(status);
    }

    private static void Observe(Task task, string operation)
    {
        _ = ObserveAsync(task, operation);

        static async Task ObserveAsync(Task task, string operation)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"{operation} failed: {ex.Message}");
            }
        }
    }
}
