using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
///     Starts and watches the verified RTSS installation, without owning the process lifetime.
///     Concurrent starts share one in-flight guard. The rationale is in
///     <c>docs\rtss.md</c> ("WSGM starts RTSS").
/// </summary>
internal sealed class RtssLauncher : IDisposable
{
    private readonly Func<string, Task<bool>> _start;
    private readonly Lock _watchGate = new();
    private readonly Func<int, Action, IDisposable?> _watchProcess;
    private bool _disposed;
    private int? _failedProcessId;
    private int? _processId;
    private int _starting;
    private IDisposable? _watch;
    private object? _watchIdentity;

    /// <summary>Creates the launcher.</summary>
    /// <param name="start">Starts the executable; injected so tests never launch anything.</param>
    /// <param name="watchProcess">Watches the verified process without owning its lifetime.</param>
    internal RtssLauncher(
        Func<string, Task<bool>>? start = null,
        Func<int, Action, IDisposable?>? watchProcess = null)
    {
        _start = start ?? StartDetachedAsync;
        _watchProcess = watchProcess ?? WatchProcess;
    }

    /// <summary>How long to wait for a started RTSS to become visible to discovery.</summary>
    /// <remarks>
    ///     RTSS takes a moment to create its shared memory and register its window. Returning before
    ///     that is indistinguishable from failing, and would make the next probe report NotRunning for
    ///     an RTSS that is simply still starting.
    /// </remarks>
    private static TimeSpan SettleTimeout { get; } = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        lock (_watchGate)
        {
            _disposed = true;
            _watchIdentity = null;
            _processId = null;
            _watch?.Dispose();
            _watch = null;
        }
    }

    /// <summary>Decides whether a probe result means WSGM should start RTSS.</summary>
    /// <param name="probe">The most recent probe.</param>
    /// <param name="enabled">Whether the user has performance control switched on.</param>
    /// <returns>Whether to start it.</returns>
    /// <remarks>
    ///     Deliberately only <see cref="RtssAvailability.NotRunning" />. That state means discovery
    ///     already accepted the installation and found no process — the one case starting it fixes.
    ///     Not installed, incompatible and degraded are all states a launch cannot improve, and starting
    ///     a program because WSGM could not identify it would be exactly the wrong response.
    /// </remarks>
    internal static bool ShouldStart(RtssProbe probe, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return enabled
               && probe.Availability is RtssAvailability.NotRunning
               && !string.IsNullOrWhiteSpace(probe.ExecutablePath);
    }

    /// <summary>Starts RTSS if it is needed, not running, and no start is settling.</summary>
    /// <param name="probe">The most recent probe.</param>
    /// <param name="enabled">Whether the user has performance control switched on.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>Whether a start was attempted and appeared to succeed.</returns>
    internal async Task<bool> TryStartAsync(
        RtssProbe probe,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldStart(probe, enabled))
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
        {
            return false;
        }

        var created = false;
        try
        {
            var executable = probe.ExecutablePath!;
            Task<bool> start;
            lock (_watchGate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                Log.Info($"RTSS is installed but not running; starting it: {executable}");
                start = _start(executable);
            }

            var started = await start.ConfigureAwait(false);
            if (!started)
            {
                Log.Warn(
                    "RTSS did not start; performance controls stay unavailable until the next "
                    + "attempt.");
                return false;
            }

            created = true;
            // Keep admission closed while the created process initializes its shared memory.
            await Task.Delay(SettleTimeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return created;
        }
        catch (Exception ex)
        {
            // Never fatal. RTSS is a feature WSGM uses, not one it is: a shell that failed to boot
            // because a frame limiter would not start would be a much worse outcome.
            Log.Warn($"Starting RTSS failed: {ex.Message}");
            return false;
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
        }
    }

    internal void Watch(RtssProbe probe, Action exited)
    {
        if (probe.Availability != RtssAvailability.Ready || probe.ProcessId is not { } processId)
        {
            return;
        }

        lock (_watchGate)
        {
            if (_disposed || _processId == processId || _failedProcessId == processId)
            {
                return;
            }

            _watchIdentity = null;
            _watch?.Dispose();
            _watch = null;
            _processId = processId;
            _failedProcessId = null;
            var identity = new object();
            _watchIdentity = identity;
            try
            {
                var watch = _watchProcess(processId, () => ProcessExited(identity, exited));
                // A process can exit synchronously while the subscription is being installed.
                if (_watchIdentity == identity)
                {
                    _watch = watch;
                    if (watch is null)
                    {
                        _processId = null;
                        _failedProcessId = processId;
                        _watchIdentity = null;
                    }
                }
                else
                {
                    watch?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _processId = null;
                _failedProcessId = processId;
                _watchIdentity = null;
                Log.Warn($"RTSS exit watch unavailable for process {processId}: {ex.Message}");
            }
        }
    }

    private void ProcessExited(object identity, Action exited)
    {
        lock (_watchGate)
        {
            if (_disposed || _watchIdentity != identity)
            {
                return;
            }

            _watchIdentity = null;
            _processId = null;
            _watch?.Dispose();
            _watch = null;
        }

        try
        {
            exited();
        }
        catch (Exception ex)
        {
            Log.Warn($"RTSS exit refresh could not start: {ex.Message}");
        }
    }

    private static IDisposable WatchProcess(int processId, Action exited)
    {
        var process = Process.GetProcessById(processId);
        try
        {
            process.Exited += (_, _) => exited();
            process.EnableRaisingEvents = true;
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>Starts RTSS detached, so it outlives WSGM rather than dying with it.</summary>
    /// <param name="executable">The verified RTSS executable.</param>
    /// <returns>Whether the process was created.</returns>
    /// <remarks>
    ///     Started with its own install directory as the working directory, which is what RTSS's own
    ///     shortcut does; it loads plugins and profiles relative to it. <c>UseShellExecute</c> is false
    ///     so no window is created and WSGM does not hand it a shell verb.
    /// </remarks>
    private static Task<bool> StartDetachedAsync(string executable)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty
        };
        using var process = Process.Start(start);
        return Task.FromResult(process is not null);
    }
}
