using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>A logical fan and the writable duty bounds its current descriptor declares.</summary>
internal readonly record struct DeviceFanCurveFan(string InstanceId, int MinimumDuty = 0, int MaximumDuty = 100);

/// <summary>A temperature observation, including the time its source actually sampled it.</summary>
internal readonly record struct DeviceFanCurveTemperature(double? Celsius, DateTimeOffset CapturedAt);

/// <summary>An acknowledged duty write; refused and uncertain outcomes both stop automatic writes.</summary>
internal readonly record struct DeviceFanCurveWriteResult(bool Applied, string? Reason = null);

/// <summary>The session-owned software fan policy; hardware access remains with the coordinator.</summary>
internal sealed class DeviceFanCurveController : IAsyncDisposable
{
    private readonly Action<string?> _availabilityChanged;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly TimeSpan _maximumTemperatureAge;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, DeviceFanCurveTemperature> _readTemperature;
    private readonly Func<string, CancellationToken, ValueTask>? _releaseAutomatic;
    private readonly Func<CancellationToken, ValueTask>? _waitTick;
    private readonly Func<string, int, CancellationToken, ValueTask<DeviceFanCurveWriteResult>> _writeDuty;
    private bool _availabilityPublished;
    private CurvePoint[] _curve = [];
    private bool _disposed;
    private bool _enabled;
    private string? _failure;
    private Fan[] _fans = [];
    private string? _lastAvailability;
    private CancellationTokenSource? _loopCancellation;
    private bool _suspended;

    internal DeviceFanCurveController(Func<string, DeviceFanCurveTemperature> readTemperature,
        Func<string, int, CancellationToken, ValueTask<DeviceFanCurveWriteResult>> writeDuty,
        Action<string?> availabilityChanged,
        Func<string, CancellationToken, ValueTask>? releaseAutomatic = null,
        Func<DateTimeOffset>? now = null,
        Func<CancellationToken, ValueTask>? waitTick = null,
        TimeSpan? maximumTemperatureAge = null)
    {
        _readTemperature = readTemperature;
        _writeDuty = writeDuty;
        _availabilityChanged = availabilityChanged;
        _releaseAutomatic = releaseAutomatic;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _waitTick = waitTick;
        _maximumTemperatureAge = maximumTemperatureAge ?? TimeSpan.FromSeconds(3);
        if (_maximumTemperatureAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTemperatureAge));
        }
    }

    /// <summary>The tracked loop, including its last write and cancellation cleanup.</summary>
    internal Task Completion { get; private set; } = Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            await JoinLoopAsync().ConfigureAwait(false);
            await ReleaseAllAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Replaces the selected curve and current fan descriptors; changes apply immediately.</summary>
    /// <remarks>Only explicit selection or integration re-enable clears a failed-write latch.</remarks>
    internal async Task ConfigureAsync(IReadOnlyList<CurvePoint>? curve, IReadOnlyList<DeviceFanCurveFan> fans,
        bool enabled, bool explicitSelection = false)
    {
        ArgumentNullException.ThrowIfNull(fans);
        var points = curve?.ToArray() ?? [];
        Validate(points, fans);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!explicitSelection && enabled == _enabled && points.AsSpan().SequenceEqual(_curve)
                && fans.Count == _fans.Length && fans.SequenceEqual(_fans.Select(fan => fan.Definition)))
            {
                return;
            }

            await JoinLoopAsync().ConfigureAwait(false);
            var reenabled = enabled && !_enabled;
            if (explicitSelection || reenabled)
            {
                _failure = null;
            }

            var next = new Fan[fans.Count];
            for (var i = 0; i < fans.Count; i++)
            {
                next[i] = _fans.FirstOrDefault(fan => fan.Definition == fans[i]) ?? new Fan(fans[i]);
            }

            foreach (var fan in _fans)
            {
                if (!enabled || points.Length == 0 || !next.Contains(fan))
                {
                    await ReleaseAsync(fan, CancellationToken.None).ConfigureAwait(false);
                }
            }

            _fans = next;
            _curve = points;
            _enabled = enabled;
            StartLoop();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async Task SuspendAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _suspended = true;
            await JoinLoopAsync().ConfigureAwait(false);
            await ReleaseAllAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async Task ResumeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _suspended = false;
            StartLoop();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _enabled = false;
            await JoinLoopAsync().ConfigureAwait(false);
            await ReleaseAllAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void StartLoop()
    {
        if (!_enabled || _suspended || _disposed || _curve.Length == 0)
        {
            return;
        }

        if (_failure is not null)
        {
            PublishAvailability(_failure);
            return;
        }

        if (_fans.Length == 0)
        {
            PublishAvailability("Software fan control needs a controllable fan.");
            return;
        }

        if (!Completion.IsCompleted)
        {
            return;
        }

        _loopCancellation = new CancellationTokenSource();
        var token = _loopCancellation.Token;
        Completion = Task.Run(() => RunAsync(token));
    }

    private async Task JoinLoopAsync()
    {
        _loopCancellation?.Cancel();
        await Completion.ConfigureAwait(false);
        _loopCancellation?.Dispose();
        _loopCancellation = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string? unavailable = null;
                foreach (var fan in _fans)
                {
                    token.ThrowIfCancellationRequested();
                    var sample = _readTemperature(fan.Definition.InstanceId);
                    var age = _now() - sample.CapturedAt;
                    if (sample.Celsius is not { } temperature || !double.IsFinite(temperature)
                                                              || age < TimeSpan.Zero || age > _maximumTemperatureAge)
                    {
                        unavailable ??= fan.UnavailableReason;
                        await ReleaseAsync(fan, token).ConfigureAwait(false);
                        if (_failure is not null)
                        {
                            return;
                        }

                        continue;
                    }

                    var duty = Interpolate(_curve, temperature, fan.Definition.MinimumDuty, fan.Definition.MaximumDuty);
                    if (fan.LastDuty == duty)
                    {
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    DeviceFanCurveWriteResult result;
                    try
                    {
                        result = await _writeDuty(fan.Definition.InstanceId, duty, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        result = new DeviceFanCurveWriteResult(false, ex.Message);
                    }

                    if (!result.Applied)
                    {
                        _failure = result.Reason ?? "The fan write was not confirmed. Select the curve again to retry.";
                        PublishAvailability(_failure);
                        return;
                    }

                    fan.LastDuty = duty;
                    fan.Released = false;
                }

                PublishAvailability(unavailable);
                if (_waitTick is { } wait)
                {
                    await wait(token).ConfigureAwait(false);
                }
                else if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _failure = "Software fan control stopped: " + ex.Message;
            PublishAvailability(_failure);
            await ReleaseAllAsync().ConfigureAwait(false);
        }
    }

    private async Task ReleaseAllAsync()
    {
        foreach (var fan in _fans)
        {
            await ReleaseAsync(fan, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseAsync(Fan fan, CancellationToken token)
    {
        if (fan.Released)
        {
            return;
        }

        fan.Released = true;
        fan.LastDuty = null;
        if (_releaseAutomatic is not { } release)
        {
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await release(fan.Definition.InstanceId, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _failure = "Automatic fan mode could not be restored: " + ex.Message;
            PublishAvailability(_failure);
        }
    }

    private void PublishAvailability(string? reason)
    {
        if (!_availabilityPublished || !string.Equals(reason, _lastAvailability, StringComparison.Ordinal))
        {
            _availabilityPublished = true;
            _lastAvailability = reason;
            _availabilityChanged(reason);
        }
    }

    /// <summary>Evaluates a validated curve linearly and clamps to this logical fan's declared bounds.</summary>
    internal static int Interpolate(IReadOnlyList<CurvePoint> curve, double temperature, int minimumDuty = 0,
        int maximumDuty = 100)
    {
        var output = (double)curve[0].Output;
        for (var i = 1; i < curve.Count; i++)
        {
            var lower = curve[i - 1];
            var upper = curve[i];
            if (temperature <= lower.Input)
            {
                break;
            }

            output = upper.Output;
            if (temperature < upper.Input)
            {
                output = lower.Output + (temperature - lower.Input) * (upper.Output - lower.Output)
                    / (upper.Input - lower.Input);
                break;
            }
        }

        return Math.Clamp((int)Math.Round(output, MidpointRounding.AwayFromZero), minimumDuty, maximumDuty);
    }

    private static void Validate(IReadOnlyList<CurvePoint> curve, IReadOnlyList<DeviceFanCurveFan> fans)
    {
        for (var i = 0; i < curve.Count; i++)
        {
            if (curve[i].Output is < 0 or > 100 || (i > 0 && curve[i].Input <= curve[i - 1].Input))
            {
                throw new ArgumentException(
                    "Fan curves need ascending temperatures and duty percentages from 0 to 100.", nameof(curve));
            }
        }

        HashSet<string> instances = [];
        foreach (var fan in fans)
        {
            if (fan.InstanceId is null || !instances.Add(fan.InstanceId) || fan.MinimumDuty < 0
                || fan.MaximumDuty > 100 || fan.MinimumDuty > fan.MaximumDuty)
            {
                throw new ArgumentException("Fans need distinct logical instances and valid duty bounds.",
                    nameof(fans));
            }
        }
    }

    private sealed class Fan(DeviceFanCurveFan definition)
    {
        internal DeviceFanCurveFan Definition { get; } = definition;

        internal string UnavailableReason { get; } =
            "A fresh temperature reading is unavailable for fan " + definition.InstanceId + ".";

        internal int? LastDuty { get; set; }
        internal bool Released { get; set; }
    }
}
