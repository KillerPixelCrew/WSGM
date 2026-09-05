using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>UI-thread projection of the shared preset service for one open overlay.</summary>
internal sealed class DevicePowerPresetSelection(DevicePowerPresets service, bool readOnly, DevicePowerAssignments? assignments = null) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    internal event Action? Changed;
    internal DevicePowerPresetState State { get; private set; } = new([], false, string.Empty, string.Empty);
    internal bool Busy { get; private set; }
    internal bool CanSelect => !_disposed && !readOnly && !Busy && State.Available;
    internal bool CanAssign => !_disposed && !readOnly && !Busy && State.Presets.Count > 0 && assignments is not null;
    internal DevicePowerAssignmentState? Assignments { get; private set; }

    internal async Task AssignAsync(bool ac, string? id)
    {
        if (!CanAssign) { return; }
        Busy = true;
        Changed?.Invoke();
        CancellationToken token = _lifetime.Token;
        try
        {
            await assignments!.AssignAsync(ac, id, token);
            var state = await service.ReadAsync(token);
            if (!_disposed) { State = state; Assignments = assignments.Snapshot(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_disposed) { State = State with { Status = ex.Message }; } }
        finally
        {
            Busy = false;
            if (!_disposed) { Changed?.Invoke(); }
            else { _lifetime.Dispose(); }
        }
    }

    internal Task RefreshAsync() => RunAsync(null);
    internal Task ApplyAsync(string id) => CanSelect ? RunAsync(id) : Task.CompletedTask;

    private async Task RunAsync(string? id)
    {
        if (_disposed || Busy) { return; }
        Busy = true;
        CancellationToken token = _lifetime.Token;
        if (id is not null) { Changed?.Invoke(); }
        try
        {
            if (id is not null) { await service.ApplyAsync(id, token); }
            DevicePowerPresetState state = await service.ReadAsync(token);
            if (!_disposed) { State = state; Assignments = assignments?.Snapshot(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            Busy = false;
            if (!_disposed) { Changed?.Invoke(); }
            else { _lifetime.Dispose(); }
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _lifetime.Cancel();
        if (!Busy) { _lifetime.Dispose(); }
        // In-flight work owns its token until it completes.
        Changed = null;
    }
}
