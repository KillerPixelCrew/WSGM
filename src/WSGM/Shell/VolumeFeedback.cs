using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Plays the shared, rate-limited and non-backlogging volume preview
/// sound used by both hardware buttons and the taskbar slider.</summary>
internal static class VolumeFeedback
{
    private const long MinimumIntervalMs = 90;
    private static long _lastRequestedAt;
    private static int _helperUnavailable;
    private static int _initializationState;
    private static int _reinitializeRequested;
    private static int _reinitializeWorkerRunning;

    /// <summary>Preopens the native playback stream away from the UI thread, so
    /// the first volume input never pays the device-open latency.</summary>
    internal static void Initialize()
    {
        if (Volatile.Read(ref _helperUnavailable) != 0
            || Interlocked.CompareExchange(ref _initializationState, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(InitializeCore);
    }

    /// <summary>Reopens the mapped playback stream after the system default
    /// output changes. Requests coalesce, but one arriving during an open is
    /// retained so the final stream always follows the newest default.</summary>
    internal static void Reinitialize()
    {
        if (Volatile.Read(ref _helperUnavailable) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _reinitializeRequested, 1);
        Interlocked.Exchange(ref _initializationState, 1);
        StartReinitializeWorker();
    }

    private static void StartReinitializeWorker()
    {
        if (Interlocked.CompareExchange(ref _reinitializeWorkerRunning, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            while (Interlocked.Exchange(ref _reinitializeRequested, 0) != 0)
            {
                InitializeCore();
            }
            Interlocked.Exchange(ref _reinitializeWorkerRunning, 0);
            // Close the race where a request arrived after the loop's final
            // exchange but before the worker marked itself idle.
            if (Volatile.Read(ref _reinitializeRequested) != 0)
            {
                StartReinitializeWorker();
            }
        });
    }

    private static void InitializeCore()
    {
        try
        {
            var result = NativeVolumeControl.InitializeFeedback();
            if (result < 0)
            {
                Log.Warn($"Volume feedback initialization failed (HRESULT 0x{result:X8}).");
                Interlocked.Exchange(ref _initializationState, 0);
            }
            else
            {
                Interlocked.Exchange(ref _initializationState, 2);
            }
        }
        catch (DllNotFoundException ex)
        {
            Disable(ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            Disable(ex);
        }
    }

    /// <summary>Requests one soft feedback sound. Calls are paced to the cue
    /// length, and the native helper drops any overlap, so held controls cannot
    /// build a delayed playback queue.</summary>
    internal static void Play()
    {
        Initialize();
        if (Volatile.Read(ref _helperUnavailable) != 0)
        {
            return;
        }

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

        try
        {
            var result = NativeVolumeControl.PlayFeedback();
            if (result < 0)
            {
                Log.Warn($"Volume feedback sound failed (HRESULT 0x{result:X8}).");
                Interlocked.Exchange(ref _initializationState, 0);
            }
        }
        catch (DllNotFoundException ex)
        {
            Disable(ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            Disable(ex);
        }
    }

    private static void Disable(Exception ex)
    {
        Interlocked.Exchange(ref _initializationState, 0);
        if (Interlocked.Exchange(ref _helperUnavailable, 1) == 0)
        {
            Log.Error("Volume feedback sound disabled: WSGM.VolumeControl.dll is unavailable.", ex);
        }
    }
}
