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
    private bool _refreshing;
    private long _revision;
    internal event Action? Changed;
    internal DevicePowerPresetState State { get; private set; } = new([], false, string.Empty, string.Empty);
    internal bool Busy { get; private set; }
    internal bool CanAssign => !_disposed && !readOnly && !Busy && State.Presets.Count > 0 && assignments is not null;
    internal DevicePowerAssignmentState? Assignments { get; private set; }

    internal async Task AssignAsync(bool ac, string? id)
    {
        if (!CanAssign) { return; }
        _revision++;
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
            else if (!_refreshing) { _lifetime.Dispose(); }
        }
    }

    internal async Task RefreshAsync()
    {
        if (_disposed || Busy || _refreshing) { return; }
        _refreshing = true;
        long revision = _revision;
        CancellationToken token = _lifetime.Token;
        try
        {
            var state = await service.ReadAsync(token);
            if (!_disposed && !Busy && revision == _revision)
            {
                State = state;
                Assignments = assignments?.Snapshot();
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _refreshing = false;
            if (_disposed && !Busy) { _lifetime.Dispose(); }
        }
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _lifetime.Cancel();
        if (!Busy && !_refreshing) { _lifetime.Dispose(); }
        // In-flight work owns its token until it completes.
        Changed = null;
    }
}
