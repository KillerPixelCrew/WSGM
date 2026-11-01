using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The Overlay hotkeys' list and commands, also supplied by inert headless preview fixtures.</summary>
internal interface IHotkeysSource : IChangeSource
{
    /// <summary>The hotkeys in display order.</summary>
    /// <returns>Detached copies.</returns>
    HotkeyState[] ReadState();

    /// <summary>Adds a hotkey, or replaces the stored one with the same id.</summary>
    /// <param name="definition">The edited definition; it needs at least one key.</param>
    /// <returns>Completion once it is stored; a refusal faults with the reason.</returns>
    Task SaveAsync(HotkeyDefinition definition);

    /// <summary>Deletes a hotkey and its Quick access pin.</summary>
    /// <param name="id">The hotkey.</param>
    /// <returns>Completion once it is stored.</returns>
    Task RemoveAsync(string id);

    /// <summary>
    ///     Sends a hotkey to the game in front. Called only after the Overlay has closed and released its input
    ///     lease; nothing is typed unless a game is in front.
    /// </summary>
    /// <param name="id">The hotkey.</param>
    /// <param name="returnTo">The window that was in front when the Overlay opened, or 0.</param>
    /// <param name="cancellationToken">Ends the wait when the Overlay reopens or the session shuts down.</param>
    /// <returns>Completion once the keys were sent or refused; the outcome is the hotkey's notice.</returns>
    Task SendAsync(string id, nint returnTo, CancellationToken cancellationToken);

    /// <summary>Records a failure the send itself could not report.</summary>
    /// <param name="id">The hotkey.</param>
    /// <param name="message">What went wrong.</param>
    void ReportFailure(string id, string message);
}
