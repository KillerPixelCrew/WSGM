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

internal sealed class DriverFailure(string message, bool attempted = false) : Exception(message)
{
    internal bool Attempted { get; } = attempted;
}

internal abstract class DriverControl(CapabilityDescriptor descriptor)
{
    internal CapabilityDescriptor Descriptor { get; } = descriptor;
    internal string Key => Descriptor.CapabilityId + "/" + Descriptor.InstanceId;
    internal abstract CapabilityValue Read();
    internal abstract void Write(CapabilityValue value);

    internal bool Accepts(CapabilityValue? value)
    {
        if (value is null || value.Kind != Descriptor.ValueKind || !Descriptor.SupportsWrite)
        {
            return false;
        }
        return value.Kind switch
        {
            CapabilityValueKind.Boolean => value.BooleanValue is not null,
            CapabilityValueKind.Choice => Descriptor.Choices.Any(choice => choice.Id == value.ChoiceValue),
            CapabilityValueKind.Integer => value.IntegerValue is { } number && number >= Descriptor.Minimum
                && number <= Descriptor.Maximum && (number - Descriptor.Minimum) % (Descriptor.Step ?? 1) == 0,
            _ => false
        };
    }
}

/// <summary>Serialized resident driver ownership shared by the AMD and NVIDIA package assemblies.</summary>
internal sealed class DriverRuntime(string id, Func<string, IDriverSession> open) : IPlugin, ICapabilityPlugin
{
    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly Dictionary<string, (CapabilityValue? Value, DateTimeOffset At)> _published = [];
    private PluginContext? _context;
    private IPluginHost? _host;
    private ICapabilityHost? _capabilities;
    private IDriverSession? _session;
    private DriverModel _model = new([], []);
    private string? _fingerprint;
    private long _generation;
    private long _cycle;
    private CancellationTokenSource? _observation;
    private Task? _loop;
    private volatile bool _running;
    private bool _disposed;

    public string Id => id;

    public async ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host = host;
        _context = context;
        _capabilities = host.Capabilities ?? throw new InvalidOperationException("The host supplied no GPU capability channel.");
        _cycle = _capabilities.CycleGeneration;
        _fingerprint = null;
        _running = true;
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => ObserveAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _running = false;
            throw;
        }
        finally
        {
            ReleaseLane();
        }
        _observation = new CancellationTokenSource();
        _loop = ObserveLoopAsync(_observation.Token);
        return _session is null ? PluginHealth.Unavailable : PluginHealth.Ready;
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
        _observation?.Cancel();
        if (_loop is { } loop)
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Close();
            await PublishModelAsync(new DriverModel([], []), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lane.Release();
        }
        _observation?.Dispose();
        _observation = null;
        _loop = null;
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
            await StopAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command, CancellationToken cancellationToken)
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
                control.Write(command.RequestedValue!);
            }
            catch (DriverFailure failure)
            {
                return Result(command, failure.Attempted ? CommandOutcome.Indeterminate : CommandOutcome.Rejected, failure.Message);
            }
            catch (Exception error)
            {
                return Result(command, CommandOutcome.Indeterminate, error.Message);
            }
            CapabilityValue? readback = null;
            try
            {
                readback = control.Read();
            }
            catch (Exception error)
            {
                Trace(DeviceTraceLevel.Warn, error.Message);
            }
            var verified = readback == command.RequestedValue;
            await PublishValueAsync(control, command.RequestedValue, CancellationToken.None).ConfigureAwait(false);
            return Result(command, verified ? CommandOutcome.AppliedVerified : CommandOutcome.AppliedUnverified, null,
                verified ? readback : null);
        }
        finally
        {
            ReleaseLane();
        }
    }

    public async ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(ApplicationProfileSync sync, CancellationToken cancellationToken)
    {
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running || _session is null || sync.CycleGeneration != _cycle)
            {
                return new ApplicationProfileSyncResult(0, 0, []);
            }
            return _session.Sync(sync, cancellationToken);
        }
        finally
        {
            ReleaseLane();
        }
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
                        await ObserveAsync(token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ReleaseLane();
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
            _session ??= open(_context!.StateDirectory);
            await PublishModelAsync(_session.Discover(), token).ConfigureAwait(false);
            foreach (var control in _model.Controls)
            {
                try
                {
                    await PublishValueAsync(control, control.Read(), token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _capabilities!.TraceChange(DeviceTraceLevel.Warn, id, control.Key, error.Message);
                    await PublishValueAsync(control, null, token).ConfigureAwait(false);
                }
            }
            Health(PluginHealth.Ready, "Driver capabilities available; hardware validation status is documented in the package.");
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

    private async Task PublishValueAsync(DriverControl control, CapabilityValue? value, CancellationToken token)
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
            Available = value is not null, ObservedAt = now,
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed
        }, token).ConfigureAwait(false);
        _published[control.Key] = (value, now);
    }

    private void ReleaseLane()
    {
        if (!_running)
        {
            Close();
        }
        _lane.Release();
    }

    private void Close()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    private void Trace(DeviceTraceLevel level, string message) => _capabilities?.Trace(level, id, message);

    private void Health(PluginHealth health, string detail)
    {
        if (_context is { } context)
        {
            _host!.PublishHealth(new PluginHealthPublication(context.Instance, context.Generation, health, detail));
            _capabilities!.TraceChange(DeviceTraceLevel.Info, id, "health", detail);
        }
    }

    private static CapabilityCommandResult Result(CapabilityCommand command, CommandOutcome outcome, string? error,
        CapabilityValue? readback = null) => new()
    {
        CommandId = command.CommandId, Outcome = outcome, CompletedAt = DateTimeOffset.UtcNow, ReadbackValue = readback,
        Reason = error is null ? null : new CapabilityReason(CapabilityReasonCode.TransportFaulted, error)
    };
}
