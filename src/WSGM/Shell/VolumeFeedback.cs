using System;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Plays the shared, rate-limited and non-backlogging volume preview
///     sound used by both hardware buttons and the audio panel slider.
/// </summary>
internal static class VolumeFeedback
{
    private const long MinimumIntervalMs = 90;
    private static readonly Lock OpenGate = new();
    private static readonly object PlayerGate = new();
    private static long _lastRequestedAt;
    private static int _requested;
    private static int _stale;
    private static bool _disposed;
    private static WaveOutFeedback? _player;

    /// <summary>
    ///     Preopens the playback stream away from the UI thread, so the
    ///     first volume input never pays the device-open latency. Only the
    ///     first call opens; a failed open waits for the next default-output
    ///     change instead of retrying on every volume press.
    /// </summary>
    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _requested, 1) == 0)
        {
            Reinitialize();
        }
    }

    /// <summary>
    ///     Reopens the mapped playback stream after the system default
    ///     output changes. Opens are serialized, and one requested during an
    ///     open runs after it, so the final stream always follows the newest default.
    /// </summary>
    internal static void Reinitialize()
    {
        Volatile.Write(ref _requested, 1);
        Interlocked.Exchange(ref _stale, 1);
        _ = Task.Run(Open);
    }

    private static void Open()
    {
        lock (OpenGate)
        {
            if (Interlocked.Exchange(ref _stale, 0) == 0)
            {
                return;
            }

            var result = WaveOutFeedback.Open(out var replacement);
            if (result < 0 || replacement is null)
            {
                Log.Warn($"Volume feedback initialization failed (HRESULT 0x{result:X8}).");
                return;
            }

            lock (PlayerGate)
            {
                if (_disposed)
                {
                    replacement.Dispose();
                    return;
                }

                var previous = _player;
                _player = replacement;
                previous?.Dispose();
            }
        }
    }

    /// <summary>Closes the preview stream and rejects an open still in flight.</summary>
    internal static void Dispose()
    {
        lock (PlayerGate)
        {
            _disposed = true;
            _player?.Dispose();
            _player = null;
        }
    }

    /// <summary>
    ///     Requests one soft feedback sound. Calls are paced to the cue
    ///     length and overlap is dropped, so held controls cannot build a delayed
    ///     playback queue.
    /// </summary>
    internal static void Play()
    {
        Initialize();
        var now = Environment.TickCount64;
        while (true)
        {
            var previous = Volatile.Read(ref _lastRequestedAt);
            if (now - previous < MinimumIntervalMs)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastRequestedAt, now, previous) == previous)
            {
                break;
            }
        }

        if (!Monitor.TryEnter(PlayerGate))
        {
            return;
        }

        try
        {
            var result = _player?.Play() ?? 1;
            if (result >= 0)
            {
                return;
            }

            Log.Warn($"Volume feedback sound failed (HRESULT 0x{result:X8}).");
            _player?.Dispose();
            _player = null;
        }
        finally
        {
            Monitor.Exit(PlayerGate);
        }
    }
}
