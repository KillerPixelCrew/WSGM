// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.Gpu;

internal sealed record DriverModel(IReadOnlyList<CapabilitySection> Sections, IReadOnlyList<DriverControl> Controls);

internal interface IDriverSession : IDisposable
{
    DriverModel Discover();
    ApplicationProfileSyncResult Sync(ApplicationProfileSync sync, CancellationToken token);
}

internal sealed class DriverFailure(string message, bool attempted = false, bool lost = false) : Exception(message)
{
    internal bool Attempted { get; } = attempted;
    internal bool Lost { get; } = lost;
}

internal abstract class DriverControl(CapabilityDescriptor descriptor)
{
    internal CapabilityDescriptor Descriptor { get; } = descriptor;
    internal string Key => Descriptor.CapabilityId + "/" + Descriptor.InstanceId;
    internal abstract CapabilityValue Read();
    internal abstract void Write(CapabilityValue value);

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
internal sealed class DriverRuntime(string id, Func<string, Action<string, string>, IDriverSession> open)
    : IPlugin, ICapabilityPlugin
{
    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly Dictionary<string, (CapabilityValue? Value, DateTimeOffset At)> _published = [];
    private readonly Lock _stopGate = new();
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

    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running || _session is null || command.ExpectedCycleGeneration != _cycle
                || command.ExpectedDescriptorGeneration != _generation || command.Deadline.HasExpired)
            {
                return Result(command, CommandOutcome.Rejected, "The GPU generation or deadline is no longer valid.");
            }

            var control = _model.Controls.FirstOrDefault(item => item.Descriptor.CapabilityId == command.CapabilityId
                                                                 && item.Descriptor.InstanceId == command.InstanceId);
            if (control is null || !control.Accepts(command.RequestedValue))
            {
                return Result(command, CommandOutcome.Rejected, "The value is not offered by this GPU control.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Admission may originate on the UI dispatcher. All native work stays off it.
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (command.Deadline.HasExpired)
                    {
                        throw new DriverFailure("The GPU command expired before the driver call.");
                    }

                    DriverWriteScope.Run(() =>
                    {
                        control.Write(command.RequestedValue ?? CapabilityValue.None());
                        return true;
                    }, () =>
                    {
                        if (cancellationToken.IsCancellationRequested || command.Deadline.HasExpired)
                        {
                            throw new DriverFailure(
                                "The GPU command expired or was cancelled before its native write.");
                        }
                    });
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DriverFailure failure)
            {
                if (failure.Lost)
                {
                    await Task.Run(Close, CancellationToken.None).ConfigureAwait(false);
                    await PublishModelAsync(new DriverModel([], []), CancellationToken.None).ConfigureAwait(false);
                    Health(PluginHealth.Unavailable, failure.Message);
                }

                return Result(command, failure.Attempted ? CommandOutcome.Indeterminate : CommandOutcome.Rejected,
                    failure.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result(command, CommandOutcome.Rejected, "The GPU command was cancelled before dispatch.");
            }
            catch (Exception error)
            {
                return Result(command, CommandOutcome.Indeterminate, error.Message);
            }

            CapabilityValue? readback = null;
            try
            {
                if (control.Descriptor.SupportsRead)
                {
                    readback = await Task.Run(control.Read, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (DriverFailure failure) when (failure.Lost)
            {
                await Task.Run(Close, CancellationToken.None).ConfigureAwait(false);
                await PublishModelAsync(new DriverModel([], []), CancellationToken.None).ConfigureAwait(false);
                Health(PluginHealth.Unavailable, failure.Message);
                return Result(command, CommandOutcome.AppliedUnverified,
                    "The driver accepted the write and disappeared before readback: " + failure.Message);
            }
            catch (Exception error)
            {
                Trace(DeviceTraceLevel.Warn, error.Message);
            }

            var verified = control.Descriptor.SupportsRead && readback == command.RequestedValue;
            await PublishValueAsync(control, command.RequestedValue ?? CapabilityValue.None(), CancellationToken.None)
                .ConfigureAwait(false);
            return Result(command, verified ? CommandOutcome.AppliedVerified : CommandOutcome.AppliedUnverified,
                verified || !control.Descriptor.SupportsRead
                    ? null
                    : "The driver accepted the write; readback did not confirm the requested value.",
                verified ? readback : null);
        }
        finally
        {
            await ReleaseLaneAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(ApplicationProfileSync sync,
        CancellationToken cancellationToken)
    {
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running || _session is null || sync.CycleGeneration != _cycle)
            {
                return new ApplicationProfileSyncResult(0, 0, []);
            }

            try
            {
                return await Task.Run(() => DriverWriteScope.Run(() => _session.Sync(sync, cancellationToken),
                    cancellationToken.ThrowIfCancellationRequested), CancellationToken.None).ConfigureAwait(false);
            }
            catch (DriverFailure failure) when (failure.Lost)
            {
                await Task.Run(Close, CancellationToken.None).ConfigureAwait(false);
                await PublishModelAsync(new DriverModel([], []), CancellationToken.None).ConfigureAwait(false);
                Health(PluginHealth.Unavailable, failure.Message);
                return new ApplicationProfileSyncResult(0, 0,
                    [new ApplicationProfileFailure("driver", "", "", failure.Message)]);
            }
        }
        finally
        {
            await ReleaseLaneAsync().ConfigureAwait(false);
        }
    }

    public string Id => id;

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
            await Task.Run(() => ObserveAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
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

    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        return ValueTask.CompletedTask;
    }

    public async ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken)
    {
        await StopAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        await StartAsync(_host!, context, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        _running = false;
        Task retiring;
        lock (_stopGate)
        {
            if (_retiring is null || _retiring.IsCompleted)
            {
                var observation = _observation;
                var loop = _loop;
                observation?.Cancel();
                _retiring = Task.Run(() => RetireAsync(observation, loop), CancellationToken.None);
            }

            retiring = _retiring;
        }

        // A cancelled caller stops waiting. The retiring task still owns the native lane and DLL.
        await retiring.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_context is { } context)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await StopAsync(context, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                Trace(DeviceTraceLevel.Warn, "The driver is still finishing an owned call; cleanup remains queued.");
            }
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
                        await Task.Run(() => ObserveAsync(token), CancellationToken.None).ConfigureAwait(false);
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

    private async Task ObserveAsync(CancellationToken token)
    {
        try
        {
            _session ??= open(_context!.StateDirectory, (key, detail) =>
                _capabilities!.TraceChange(DeviceTraceLevel.Warn, id, key, detail));
            await PublishModelAsync(_session.Discover(), token).ConfigureAwait(false);
            foreach (var control in _model.Controls)
            {
                try
                {
                    await PublishValueAsync(control, control.Read(), token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException &&
                                              error is not DriverFailure { Lost: true })
                {
                    _capabilities!.TraceChange(DeviceTraceLevel.Warn, id, control.Key, error.Message);
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

        await _capabilities!.PublishDescriptorsAsync(new CapabilityDescriptorSet
        {
            CycleGeneration = _cycle, Generation = ++_generation, Sections = model.Sections,
            Descriptors = model.Controls.Select(control => control.Descriptor).ToArray()
        }, token).ConfigureAwait(false);
        _fingerprint = fingerprint;
        _published.Clear();
    }

    private async Task PublishValueAsync(DriverControl control, CapabilityValue? value, CancellationToken token,
        string? error = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (_published.TryGetValue(control.Key, out var previous) && previous.Value == value
                                                                  && now - previous.At < TimeSpan.FromSeconds(20))
        {
            return;
        }

        await _capabilities!.PublishCapabilityStateAsync(new CapabilityState
        {
            CapabilityId = control.Descriptor.CapabilityId, InstanceId = control.Descriptor.InstanceId,
            CycleGeneration = _cycle, DescriptorGeneration = _generation, ObservedValue = value,
            Available = value is not null || control.Descriptor.SupportsWrite || control.Descriptor.SupportsAction,
            ObservedAt = now,
            Reason = error is null ? null : new CapabilityReason(CapabilityReasonCode.TransportFaulted, error),
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed
        }, token).ConfigureAwait(false);
        _published[control.Key] = (value, now);
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
        _capabilities?.Trace(level, id, message);
    }

    private void Health(PluginHealth health, string detail)
    {
        if (_context is { } context)
        {
            _host!.PublishHealth(new PluginHealthPublication(context.Instance, context.Generation, health, detail));
            _capabilities!.TraceChange(DeviceTraceLevel.Info, id, "health", detail);
        }
    }

    private static CapabilityCommandResult Result(CapabilityCommand command, CommandOutcome outcome, string? error,
        CapabilityValue? readback = null)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId, Outcome = outcome, CompletedAt = DateTimeOffset.UtcNow,
            ReadbackValue = readback,
            Reason = error is null ? null : new CapabilityReason(CapabilityReasonCode.TransportFaulted, error)
        };
    }
}
