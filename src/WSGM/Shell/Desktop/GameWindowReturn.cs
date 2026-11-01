using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     The one way WSGM brings a game window forward: Steam's raise for the window's process, then
///     focus of the exact HWND. The Open apps switcher, the Overlay's close refocus and launched-window
///     focus all return through it.
/// </summary>
/// <remarks>
///     Big Picture draws its in-game menus in the game's overlay window only while Steam considers
///     that game focused, and otherwise in its own main window behind the game. A plain Windows focus
///     call leaves Steam's view unchanged, so Quick Access is audible but hidden. Steam's raise
///     updates it. Call it on the UI thread: the focus step runs on the caller's context and its input
///     attachment needs a message queue.
/// </remarks>
/// <param name="raiseSteamGame">
///     Requests Steam activation for the selected process; true reports call completion, not
///     overlay recovery.
/// </param>
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
        : this(raiseSteamGame, ReadProcessId, IsConsole, WindowFinder.ForceForeground, Log.Info, lifetime)
    {
    }

    /// <summary>Returns to a window owned by whichever process owns it now.</summary>
    /// <param name="hwnd">Exact window to bring forward.</param>
    /// <param name="cancellationToken">Cancels waiting or prevents the later focus attempt.</param>
    /// <returns>Whether the window became foreground; false when it no longer exists.</returns>
    internal Task<bool> ReturnAsync(nint hwnd, CancellationToken cancellationToken)
    {
        return ReturnAsync(hwnd, hwnd == 0 ? 0 : readProcessId(hwnd), cancellationToken);
    }

    /// <summary>Revalidates window ownership around Steam activation, then focuses the selected HWND.</summary>
    /// <param name="hwnd">Exact window to bring forward.</param>
    /// <param name="processId">Process identity captured with the window.</param>
    /// <param name="cancellationToken">
    ///     Cancels waiting or prevents the later focus attempt; an accepted activation may already
    ///     have taken effect.
    /// </param>
    /// <returns>
    ///     Whether the exact window became foreground; false for a stale selection or a refused focus. Outcomes are
    ///     logged; Steam overlay recovery is not verified.
    /// </returns>
    internal async Task<bool> ReturnAsync(nint hwnd, uint processId, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        cancellationToken = linked.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (hwnd == 0 || processId == 0 || readProcessId(hwnd) != processId)
        {
            log("Game return: selected window no longer belongs to the selected process.");
            return false;
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
            return false;
        }

        // Both steps always run, in this order. Steam's raise makes Steam treat the game as focused, so
        // Big Picture opens its menus over the game. It raises nothing for a window without a Steam
        // overlay: a desktop app, a non-Steam window, or a game whose overlay is not ready yet, such as
        // one second after its window appears. Even when it completes it does not prove which window
        // Windows put in front, and Steam may pick a mod loader's console. The exact-window focus
        // therefore follows either way, as it did before Steam's raise existed.
        var foreground = focus(hwnd);
        log($"Game return: pid={processId}, hwnd=0x{hwnd:X}, Steam call completed={steamCompleted}, "
            + $"foreground verified={foreground}. Overlay recovery remains unverified.");
        return foreground;
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
}
