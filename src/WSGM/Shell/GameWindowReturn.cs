using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Returns to a selected HWND after the switcher has released its surface and input.</summary>
/// <param name="raiseSteamGame">Requests Steam activation for the selected process; true reports call completion, not overlay recovery.</param>
/// <param name="readProcessId">Reads the current owner of an HWND, or zero when unavailable.</param>
/// <param name="isConsole">Identifies console windows, which bypass Steam activation.</param>
/// <param name="focus">Attempts to focus the exact HWND and reports whether it became foreground.</param>
/// <param name="log">Nonthrowing diagnostic sink.</param>
/// <param name="lifetime">Owning session cancellation linked with each return request.</param>
internal sealed class GameWindowReturn(
    Func<uint, CancellationToken, Task<bool>> raiseSteamGame,
    Func<nint, uint> readProcessId,
    Func<nint, bool> isConsole,
    Func<nint, bool> focus,
    Action<string> log,
    CancellationToken lifetime = default)
{
    /// <summary>Creates a return coordinator using Windows window-identity and focus operations.</summary>
    /// <param name="raiseSteamGame">Borrowed Steam activation operation.</param>
    /// <param name="lifetime">Owning session cancellation.</param>
    internal GameWindowReturn(Func<uint, CancellationToken, Task<bool>> raiseSteamGame, CancellationToken lifetime)
        : this(raiseSteamGame, ReadProcessId, IsConsole, Focus, Log.Info, lifetime)
    {
    }

    /// <summary>Revalidates window ownership around Steam activation, then focuses the selected HWND.</summary>
    /// <param name="hwnd">Exact window chosen by the switcher.</param>
    /// <param name="processId">Process identity captured with the selection.</param>
    /// <param name="cancellationToken">Cancels waiting or prevents the later focus attempt; an accepted activation may already have taken effect.</param>
    /// <returns>Completion of the attempt, including a no-op for a stale selection. Outcomes are logged; Steam overlay recovery is not verified.</returns>
    internal async Task ReturnAsync(nint hwnd, uint processId, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        cancellationToken = linked.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (hwnd == 0 || processId == 0 || readProcessId(hwnd) != processId)
        {
            log("Game return: selected window no longer belongs to the selected process.");
            return;
        }

        var steamCompleted = false;
        try
        {
            // A selected console remains a valid switcher destination, but it is never a reason
            // to activate the game's overlay session. The exact HWND, not a PID's main window,
            // remains authoritative even when Steam raises a mod loader's console.
            if (!isConsole(hwnd))
            {
                steamCompleted = await raiseSteamGame(processId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"Game return: Steam activation unavailable: {ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (readProcessId(hwnd) != processId)
        {
            log("Game return: selected window disappeared or changed owner during Steam activation.");
            return;
        }

        var foreground = focus(hwnd);
        log($"Game return: pid={processId}, hwnd=0x{hwnd:X}, Steam call completed={steamCompleted}, "
            + $"foreground verified={foreground}. Overlay recovery remains unverified.");
    }

    private static uint ReadProcessId(nint hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return 0;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    private static bool IsConsole(nint hwnd)
    {
        var buffer = new char[256];
        var length = NativeMethods.RealGetWindowClassW(hwnd, buffer, (uint)buffer.Length);
        string name = new(buffer, 0, (int)length);
        return name is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS";
    }

    private static bool Focus(nint hwnd)
    {
        WindowFinder.BringToForeground(hwnd);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }
}
