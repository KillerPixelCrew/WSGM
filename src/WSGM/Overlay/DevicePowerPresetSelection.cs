using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>UI-thread projection of the shared preset service for one open overlay.</summary>
internal sealed class DevicePowerPresetSelection : IDisposable
{
    private readonly DevicePowerAssignments? _assignments;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _readOnly;
    private readonly DevicePowerPresets _service;
    private bool _disposed;
    private bool _refreshing;
    private long _revision;
    internal DevicePowerPresetState State { get; private set; } = new([], false, string.Empty, string.Empty);
    internal bool Busy { get; private set; }
    /// <summary>Projects the shared preset service and follows the assignment owner's changes.</summary>
    /// <param name="service">Borrowed session preset owner.</param>
    /// <param name="readOnly">True for a preview that must not write assignments.</param>
    /// <param name="assignments">Optional shared AC/battery assignment owner; null disables assignment.</param>
    internal DevicePowerPresetSelection(DevicePowerPresets service, bool readOnly,
        DevicePowerAssignments? assignments = null)
    {
        _service = service;
        _readOnly = readOnly;
        _assignments = assignments;
        if (assignments is not null)
        {
            assignments.Changed += OnAssignmentsChanged;
        }
    }

    internal bool CanAssign => !_disposed && !_readOnly && !Busy && State.Presets.Count > 0
                               && _assignments is not null;

    internal DevicePowerAssignmentState? Assignments { get; private set; }

    /// <summary>Cancels this UI projection's lifetime and prevents later publication; borrowed session owners remain alive.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // The source has no timer, so it needs no disposal; in-flight work keeps its cancelled token.
        _disposed = true;
        if (_assignments is not null)
        {
            _assignments.Changed -= OnAssignmentsChanged;
        }

        _lifetime.Cancel();
        Changed = null;
    }

    // Raised on the assignment owner's working thread; the projection lives on the UI thread.
    private void OnAssignmentsChanged()
    {
        Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    }

    /// <summary>Raised on the captured UI context when projected state or busy status changes.</summary>
    internal event Action? Changed;

    /// <summary>Changes one AC/battery preset assignment through the shared assignment owner.</summary>
    /// <param name="ac">True for AC power, false for battery.</param>
    /// <param name="id">Preset ID, or null to clear that source assignment.</param>
    /// <returns>Completion of assignment and refresh; unavailable/read-only/busy state is a no-op.</returns>
    internal async Task AssignAsync(bool ac, string? id)
    {
        if (!CanAssign)
        {
            return;
        }

        _revision++;
        Busy = true;
        Changed?.Invoke();
        var token = _lifetime.Token;
        try
        {
            await _assignments!.AssignAsync(ac, id, token);
            var state = await _service.ReadAsync(token);
            if (!_disposed)
            {
                State = state;
                Assignments = _assignments.Snapshot();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed)
            {
                State = State with { Status = ex.Message };
            }
        }
        finally
        {
            Busy = false;
            if (!_disposed)
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Reads shared state without overlapping this projection's active operation.</summary>
    /// <returns>Completion of the refresh; disposed/busy projections are a no-op and late results are not published.</returns>
    internal async Task RefreshAsync()
    {
        if (_disposed || Busy || _refreshing)
        {
            return;
        }

        _refreshing = true;
        var revision = _revision;
        var token = _lifetime.Token;
        try
        {
            var state = await _service.ReadAsync(token);
            if (!_disposed && !Busy && revision == _revision)
            {
                State = state;
                Assignments = _assignments?.Snapshot();
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _refreshing = false;
        }
    }
}
