using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Brings a window that a running Steam app opens behind Big Picture to the front while WSGM is
///     the shell.
/// </summary>
/// <remarks>
///     Windows only lets a process take the foreground under narrow conditions, and a game, launcher
///     or installer started through Steam often misses them: a launcher's game is not a child of the
///     foreground process, and controller input during loading cancels Steam's foreground grant. The
///     window then opens behind Big Picture. Explorer would show it on the taskbar; in Game Mode there
///     is none, so it was reachable only through the overlay's app switcher.
///     <para>
///         The shell hook reports each new top-level window and each window Windows flashes after
///         refusing its focus request. After a short settle, a switchable window is raised when Big
///         Picture (or nothing) holds the foreground and Steam reports a running app. The last
///         condition keeps startup applications from covering Big Picture at boot. Each window is
///         attempted once: a refused attempt makes Windows flash the window, and that flash must not
///         start another attempt.
///     </para>
/// </remarks>
internal sealed class LaunchedWindowFocus : IDisposable
{
    /// <summary>Lets a new window finish appearing and take focus on its own first.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private readonly Func<bool> _steamAppRunning;
    private readonly MessageWindow _window;
    private bool _active;
    private bool _disposed;
    private int _generation;
    private nint _lastAttempt;

    /// <summary>Creates the inactive handler on the Avalonia UI thread.</summary>
    /// <param name="window">The process message window carrying the Game Mode shell hook.</param>
    /// <param name="steamAppRunning">Whether Steam currently reports at least one running app.</param>
    internal LaunchedWindowFocus(MessageWindow window, Func<bool> steamAppRunning)
    {
        _window = window;
        _steamAppRunning = steamAppRunning;
        _window.ShellHookReceived += OnShellHook;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _generation);
        _window.ShellHookReceived -= OnShellHook;
    }

    /// <summary>Starts or stops raising launched windows; Explorer handles them on the desktop.</summary>
    /// <param name="active">Whether WSGM is the shell. Invoke on the UI thread.</param>
    internal void SetGameModeActive(bool active)
    {
        if (_disposed || _active == active)
        {
            return;
        }

        _active = active;
        _lastAttempt = 0;
        Interlocked.Increment(ref _generation);
    }

    /// <summary>The foreground condition under which a launched window may be raised.</summary>
    /// <param name="foreground">The current foreground window, or zero.</param>
    /// <param name="foregroundIsSteam">Whether a Steam process owns that foreground window.</param>
    /// <param name="candidateIsSteam">Whether a Steam process owns the candidate window.</param>
    /// <param name="candidateIsMinimized">Whether the candidate is minimized.</param>
    /// <param name="steamAppRunning">Whether Steam reports a running app.</param>
    /// <returns>Whether the candidate should be brought to the front.</returns>
    internal static bool ShouldRaise(
        nint foreground,
        bool foregroundIsSteam,
        bool candidateIsSteam,
        bool candidateIsMinimized,
        bool steamAppRunning)
    {
        // Steam places its own windows; a minimized window was minimized on purpose.
        return (foreground == 0 || foregroundIsSteam)
               && !candidateIsSteam
               && !candidateIsMinimized
               && steamAppRunning;
    }

    private void OnShellHook(nint eventCode, nint data)
    {
        if (!_active
            || data == 0
            || eventCode is not (NativeMethods.HshellWindowCreated or NativeMethods.HshellFlash))
        {
            return;
        }

        _ = RaiseAfterSettleAsync(data, Volatile.Read(ref _generation));
    }

    private async Task RaiseAfterSettleAsync(nint window, int generation)
    {
        try
        {
            // The continuation returns to the UI thread, whose message queue the input
            // attachment in ForceForeground needs.
            await Task.Delay(SettleDelay);
            if (_disposed || generation != Volatile.Read(ref _generation) || window == _lastAttempt)
            {
                return;
            }

            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == window
                || !NativeMethods.IsWindow(window)
                || !WindowFinder.IsSwitchableWindow(window)
                || (NativeMethods.GetWindowLong(window, NativeMethods.GwlExStyle)
                    & NativeMethods.WsExNoActivate) != 0)
            {
                return;
            }

            var steam = WindowFinder.FindProcessIds(Steam.ProcessNames);
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            var foregroundProcessId = 0u;
            if (foreground != 0)
            {
                NativeMethods.GetWindowThreadProcessId(foreground, out foregroundProcessId);
            }

            var appRunning = _steamAppRunning();
            if (!ShouldRaise(
                    foreground,
                    steam.Contains(foregroundProcessId),
                    steam.Contains(processId),
                    NativeMethods.IsIconic(window),
                    appRunning))
            {
                if (!appRunning && steam.Contains(foregroundProcessId))
                {
                    // The one skip worth a line: a window that opened behind Big Picture and
                    // stayed there because Steam named no running app at the time.
                    Log.Info($"Launched window: {Describe(window, processId)} opened behind Big Picture, "
                             + "left in place because Steam reports no running app.");
                }

                return;
            }

            _lastAttempt = window;
            var raised = WindowFinder.ForceForeground(window);
            Log.Info($"Launched window: {Describe(window, processId)} opened behind "
                     + $"{(foreground == 0 ? "no foreground window" : "Big Picture")}; "
                     + $"raised, foreground verified={raised}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Launched window focus failed: {ex.Message}");
        }
    }

    private static string Describe(nint window, uint processId)
    {
        var name = NativeShellProcess.TryGetImagePath(processId) is { } path
            ? Path.GetFileName(path)
            : "unknown process";
        return $"0x{window:X} ({name}, pid {processId})";
    }
}
