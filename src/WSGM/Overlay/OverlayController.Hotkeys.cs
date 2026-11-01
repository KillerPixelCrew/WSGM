using System;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    /// <summary>
    ///     Closes the sheet, then sends a hotkey to the game it opened over once the window has closed and the
    ///     input lease is released. The game's focus is restored as on any close; the service types nothing
    ///     unless a game is in front.
    /// </summary>
    /// <param name="id">The hotkey.</param>
    private void SendHotkey(string id)
    {
        if (_disposed || _overlay is not { } window || _services.Sources.Hotkeys is not { } hotkeys)
        {
            return;
        }

        var returnTo = _restoreFocusTo;
        RunAfterClose(window, "Hotkey send", async token =>
        {
            try
            {
                await Task.Run(() => hotkeys.SendAsync(id, returnTo, token), token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hotkeys.ReportFailure(id, ex.Message);
                Log.Error("Hotkey send failed.", ex);
            }
        }, true);
    }
}
