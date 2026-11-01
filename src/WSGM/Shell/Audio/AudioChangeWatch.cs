using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Owns the Core Audio change notifications behind <see cref="AudioManager" />: one volume watch
///     per direction and the endpoint watch.
/// </summary>
/// <remarks>
///     Each volume watch is bound to the endpoint that was the default when it started, so a default
///     change restarts them. Registration and unregistration run on workers, because unregistering
///     waits for an in-flight callback and the callbacks post to the UI thread; the change callbacks
///     run on the UI thread. Start and Dispose are UI-thread calls.
/// </remarks>
internal sealed class AudioChangeWatch : IDisposable
{
    private readonly Action _endpointsChanged;
    private readonly Action _volumeChanged;
    private IDisposable? _captureVolumeWatch;
    private volatile bool _disposed;
    private IDisposable? _endpointWatch;
    private IDisposable? _renderVolumeWatch;
    private int _volumeWatchGeneration;

    /// <summary>Creates an inert watch.</summary>
    /// <param name="volumeChanged">Called on the UI thread when either direction's volume or mute changed.</param>
    /// <param name="endpointsChanged">Called on the UI thread when an endpoint or a default changed.</param>
    internal AudioChangeWatch(Action volumeChanged, Action endpointsChanged)
    {
        _volumeChanged = volumeChanged;
        _endpointsChanged = endpointsChanged;
    }

    /// <summary>Unregisters every watch off the UI thread and drops later notifications.</summary>
    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _volumeWatchGeneration);
        var watches = new[] { _endpointWatch, _renderVolumeWatch, _captureVolumeWatch };
        _endpointWatch = null;
        _renderVolumeWatch = null;
        _captureVolumeWatch = null;
        // Unregistering waits for an in-flight callback, and the callbacks post to this thread, so
        // the disposal must not run on it.
        _ = Task.Run(() =>
        {
            foreach (var watch in watches)
            {
                try
                {
                    watch?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Audio watch disposal failed: {ex.Message}");
                }
            }
        });
    }

    /// <summary>Registers the volume and endpoint watches.</summary>
    internal void Start()
    {
        StartWatches(true);
    }

    /// <summary>Registers for Core Audio change notifications, replacing the volume watches.</summary>
    /// <param name="endpoints">Whether the endpoint watch is (re)registered as well.</param>
    private void StartWatches(bool endpoints)
    {
        var generation = Interlocked.Increment(ref _volumeWatchGeneration);
        var previous = new[] { _renderVolumeWatch, _captureVolumeWatch };
        _renderVolumeWatch = null;
        _captureVolumeWatch = null;
        _ = Task.Run(() =>
        {
            foreach (var watch in previous)
            {
                watch?.Dispose();
            }

            if (_disposed || Volatile.Read(ref _volumeWatchGeneration) != generation)
            {
                return;
            }

            var render =
                CoreAudio.StartVolumeWatch(CoreAudio.AudioDirection.Render, OnVolumeNotified, out var renderWatch);
            var capture = CoreAudio.StartVolumeWatch(CoreAudio.AudioDirection.Capture, OnVolumeNotified,
                out var captureWatch);
            IDisposable? endpointWatch = null;
            var endpointResult = 0;
            if (endpoints)
            {
                endpointResult = CoreAudio.StartEndpointWatch(OnEndpointNotified, out endpointWatch);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || Volatile.Read(ref _volumeWatchGeneration) != generation)
                {
                    _ = Task.Run(() =>
                    {
                        renderWatch?.Dispose();
                        captureWatch?.Dispose();
                        endpointWatch?.Dispose();
                    });
                    return;
                }

                _renderVolumeWatch = renderWatch;
                _captureVolumeWatch = captureWatch;
                if (endpoints)
                {
                    _endpointWatch = endpointWatch;
                }

                if (render < 0 || capture < 0 || endpointResult < 0)
                {
                    Log.Change(
                        "audio-watch",
                        $"Audio change notifications unavailable (render=0x{render:X8}, capture=0x{capture:X8}, "
                        + $"endpoints=0x{endpointResult:X8}); falling back to the "
                        + $"{AudioManager.SafetyPollInterval.TotalSeconds:F0} s poll.");
                }
            });
        });
    }

    private void OnVolumeNotified()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                _volumeChanged();
            }
        });
    }

    private void OnEndpointNotified(CoreAudio.AudioEndpointWatchEvent change)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (change.Change is CoreAudio.AudioEndpointChange.DefaultChanged)
            {
                StartWatches(false);
            }

            _endpointsChanged();
        });
    }
}
