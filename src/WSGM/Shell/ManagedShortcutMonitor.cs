using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Rechecks stored content after volume, resume or explicit metadata changes.</summary>
internal sealed class ManagedShortcutMonitor : IAsyncDisposable
{
    private readonly EmulatorManager? _emulators;
    private readonly RemovableDriveManager? _drives;
    private readonly object _gate = new();
    private readonly GameLibraryService _library;
    private readonly bool _registered;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly MessageWindow _window;
    private readonly Task _worker;
    private Task? _dispose;
    private bool _disposed;
    private bool _fullCheck;

    internal ManagedShortcutMonitor(MessageWindow window, GameLibraryService library, EmulatorManager? emulators,
        RemovableDriveManager? drives = null)
    {
        _window = window;
        _library = library;
        _emulators = emulators;
        _drives = drives;
        if (drives is not null)
        {
            drives.TopologyChanged += OnStorageTopologyChanged;
        }
        _registered = window.RegisterVolumeNotifications();
        window.VolumeChanged += OnVolumeChanged;
        window.SystemResumed += Request;
        library.ManagedEntriesChanged += Request;
        if (emulators is not null)
        {
            emulators.InstallationsChanged += Request;
        }

        _worker = Task.Run(RunAsync);
        Request();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _dispose ??= DisposeCoreAsync();
            return new ValueTask(_dispose);
        }
    }

    private void OnVolumeChanged(bool arrived)
    {
        Wake(full: false);
    }

    private void OnStorageTopologyChanged()
    {
        Wake(full: false);
    }

    private void Request()
    {
        Wake(full: true);
    }

    private void Wake(bool full)
    {
        lock (_gate)
        {
            _fullCheck |= full;
            if (!_disposed && (_emulators is null || _emulators.Initialized)
                           && _wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var signalled = await _wake.WaitAsync(TimeSpan.FromMinutes(5), _stop.Token).ConfigureAwait(false);
                bool full;
                lock (_gate)
                {
                    full = _fullCheck || !signalled;
                    _fullCheck = false;
                }
                // Native notifications arrive in bursts, often before a newly mounted filesystem is ready.
                await Task.Delay(TimeSpan.FromMilliseconds(750), _stop.Token).ConfigureAwait(false);
                try
                {
                    var result = await (full
                            ? _library.RecheckAvailabilityAsync("", _stop.Token)
                            : _library.RecheckStorageChangesAsync(_stop.Token)).WaitAsync(_stop.Token)
                        .ConfigureAwait(false);
                    if (!result.Succeeded)
                    {
                        Log.Change("library.availability.error", result.Error ?? "Availability could not be checked.");
                    }
                    if (!full)
                    {
                        // One bounded read-only observation recovers an arrival that was not ready at the first settle.
                        await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
                        await _library.RecheckStorageChangesAsync(_stop.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                                               or UnauthorizedAccessException or InvalidOperationException
                                               or ImportStateException)
                {
                    Log.Change("library.availability.error", $"Availability could not be checked: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (_drives is not null)
        {
            _drives.TopologyChanged -= OnStorageTopologyChanged;
        }

        _library.ManagedEntriesChanged -= Request;
        if (_emulators is not null)
        {
            _emulators.InstallationsChanged -= Request;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _window.VolumeChanged -= OnVolumeChanged;
            _window.SystemResumed -= Request;
            if (_registered)
            {
                _window.DeregisterVolumeNotifications();
            }
        });
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        LibraryBadges.UpdateShortcuts([]);
        _wake.Dispose();
        _stop.Dispose();
    }
}
