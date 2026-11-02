using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
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

    /// <summary>The row projection Steam's Device performance rows draw, read through <see cref="PowerPresetRows" />.</summary>
    internal PowerPresetRowState State { get; private set; } = new(false, [], string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty, "Manual selection");

    internal bool Busy { get; private set; }

    internal bool CanAssign => !_disposed && !_readOnly && !Busy && State is { Available: true, Options.Count: > 0 };

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

    /// <summary>The last command's refusal, kept until the next command so a management view can show it.</summary>
    internal string? LastError { get; private set; }

    /// <summary>Changes one AC/battery preset assignment through the shared assignment owner.</summary>
    /// <param name="ac">True for AC power, false for battery.</param>
    /// <param name="id">Preset ID, or null to clear that source assignment.</param>
    /// <returns>Completion of assignment and refresh; unavailable/read-only/busy state is a no-op.</returns>
    internal Task AssignAsync(bool ac, string? id)
    {
        return CanAssign
            ? RunAsync(token => PowerPresetRows.AssignAsync(_assignments, ac, id, token))
            : Task.CompletedTask;
    }

    /// <summary>Whether saved presets can be created, renamed, updated or deleted now.</summary>
    internal bool CanEdit => !_disposed && !_readOnly && !Busy && _assignments is not null
                             && State.Options.Count > 0;

    /// <summary>Saves the current Custom reading as a new preset; nothing changes on the device.</summary>
    /// <param name="name">The name the user typed.</param>
    /// <returns>Completion of the save and refresh; a refusal is shown in the status line.</returns>
    internal Task SaveAsync(string name)
    {
        return CanEdit ? RunAsync(token => PowerPresetRows.SaveAsync(_assignments, name, token)) : Task.CompletedTask;
    }

    /// <summary>Renames a saved preset.</summary>
    /// <param name="id">The saved preset.</param>
    /// <param name="name">The name the user typed.</param>
    /// <returns>Completion of the rename and refresh; a refusal is shown in the status line.</returns>
    internal Task RenameAsync(string id, string name)
    {
        return CanEdit
            ? RunAsync(token => PowerPresetRows.RenameAsync(_assignments, id, name, token))
            : Task.CompletedTask;
    }

    /// <summary>Replaces a saved preset's values with the current Custom reading.</summary>
    /// <param name="id">The saved preset.</param>
    /// <returns>Completion of the update and refresh; a refusal is shown in the status line.</returns>
    internal Task UpdateAsync(string id)
    {
        return CanEdit ? RunAsync(token => PowerPresetRows.UpdateAsync(_assignments, id, token)) : Task.CompletedTask;
    }

    /// <summary>Deletes a saved preset; assignments that use it keep its values as Custom.</summary>
    /// <param name="id">The saved preset.</param>
    /// <returns>Completion of the delete and refresh; a refusal is shown in the status line.</returns>
    internal Task DeleteAsync(string id)
    {
        return CanEdit ? RunAsync(token => PowerPresetRows.DeleteAsync(_assignments, id, token)) : Task.CompletedTask;
    }

    private async Task RunAsync(Func<CancellationToken, Task<SteamUiCommandResult>> command)
    {
        _revision++;
        Busy = true;
        Changed?.Invoke();
        var token = _lifetime.Token;
        try
        {
            var result = await command(token);
            var state = await PowerPresetRows.ReadAsync(_service, _assignments);
            if (!_disposed)
            {
                LastError = result.Succeeded ? null : result.Error;
                State = result.Succeeded || string.IsNullOrEmpty(result.Error)
                    ? state
                    : state with { StatusText = result.Error };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed)
            {
                LastError = ex.Message;
                State = State with { StatusText = ex.Message };
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
        try
        {
            var state = await PowerPresetRows.ReadAsync(_service, _assignments);
            if (!_disposed && !Busy && revision == _revision)
            {
                State = state;
                Changed?.Invoke();
            }
        }
        finally
        {
            _refreshing = false;
        }
    }
}
