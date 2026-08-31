using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Starts the verified RTSS installation when WSGM needs it and it is not running — only ever the
/// executable discovery verified, and only once per session. The rationale is in
/// <c>docs\rtss.md</c> ("WSGM starts RTSS").
/// </summary>
internal sealed class RtssLauncher
{
    /// <summary>How long to wait for a started RTSS to become visible to discovery.</summary>
    /// <remarks>
    /// RTSS takes a moment to create its shared memory and register its window. Returning before
    /// that is indistinguishable from failing, and would make the next probe report NotRunning for
    /// an RTSS that is simply still starting.
    /// </remarks>
    internal static TimeSpan SettleTimeout { get; } = TimeSpan.FromSeconds(10);

    private readonly Func<string, Task<bool>> _start;
    private int _attempted;

    /// <summary>Creates the launcher.</summary>
    /// <param name="start">Starts the executable; injected so tests never launch anything.</param>
    internal RtssLauncher(Func<string, Task<bool>>? start = null)
    {
        _start = start ?? StartDetachedAsync;
    }

    /// <summary>Whether this session has already tried to start RTSS.</summary>
    internal bool Attempted => Volatile.Read(ref _attempted) != 0;

    /// <summary>Decides whether a probe result means WSGM should start RTSS.</summary>
    /// <param name="probe">The most recent probe.</param>
    /// <param name="enabled">Whether the user has performance control switched on.</param>
    /// <returns>Whether to start it.</returns>
    /// <remarks>
    /// Deliberately only <see cref="RtssAvailability.NotRunning"/>. That state means discovery
    /// already accepted the installation and found no process — the one case starting it fixes.
    /// Not installed, incompatible and degraded are all states a launch cannot improve, and starting
    /// a program because WSGM could not identify it would be exactly the wrong response.
    /// </remarks>
    internal static bool ShouldStart(RtssProbe probe, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return enabled
            && probe.Availability is RtssAvailability.NotRunning
            && !string.IsNullOrWhiteSpace(probe.ExecutablePath);
    }

    /// <summary>Starts RTSS once, if this probe says it is needed and not running.</summary>
    /// <param name="probe">The most recent probe.</param>
    /// <param name="enabled">Whether the user has performance control switched on.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>Whether a start was attempted and appeared to succeed.</returns>
    internal async Task<bool> TryStartAsync(
        RtssProbe probe,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldStart(probe, enabled) || Interlocked.Exchange(ref _attempted, 1) != 0)
        {
            return false;
        }

        string executable = probe.ExecutablePath!;
        Log.Info($"RTSS is installed but not running; starting it: {executable}");
        try
        {
            bool started = await _start(executable).ConfigureAwait(false);
            if (!started)
            {
                Log.Warn("RTSS did not start; performance controls stay unavailable this session.");
                return false;
            }
        }
        catch (Exception ex)
        {
            // Never fatal. RTSS is a feature WSGM uses, not one it is: a shell that failed to boot
            // because a frame limiter would not start would be a much worse outcome.
            Log.Warn($"Starting RTSS failed: {ex.Message}");
            return false;
        }

        // Reported rather than awaited here: the caller's next poll is what confirms it, and this
        // only gives RTSS the room to get there before that poll calls it missing again.
        try
        {
            await Task.Delay(SettleTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return true;
        }

        return true;
    }

    /// <summary>Starts RTSS detached, so it outlives WSGM rather than dying with it.</summary>
    /// <param name="executable">The verified RTSS executable.</param>
    /// <returns>Whether the process was created.</returns>
    /// <remarks>
    /// Started with its own install directory as the working directory, which is what RTSS's own
    /// shortcut does; it loads plugins and profiles relative to it. <c>UseShellExecute</c> is false
    /// so no window is created and WSGM does not hand it a shell verb.
    /// </remarks>
    private static Task<bool> StartDetachedAsync(string executable)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(executable) ?? string.Empty,
        };
        using Process? process = Process.Start(start);
        return Task.FromResult(process is not null);
    }
}
