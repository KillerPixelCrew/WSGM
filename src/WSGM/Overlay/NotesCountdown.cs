using Avalonia.Threading;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The session's one countdown display; its window is created on first use, on the UI thread.</summary>
/// <remarks>Notes that never takes a screenshot never creates a window.</remarks>
internal sealed class NotesCountdown : INotesCountdownDisplay
{
    private NotesCountdownWindow? _window;

    /// <inheritdoc />
    public void Show(int remaining, string game)
    {
        Dispatcher.UIThread.Post(() => (_window ??= new NotesCountdownWindow()).Show(remaining, game));
    }

    /// <inheritdoc />
    public void Hide()
    {
        // Display capture must not start while the countdown is still waiting to hide on the UI thread.
        Dispatcher.UIThread.Invoke(() => _window?.Hide());
    }

    /// <summary>Closes the window at session end.</summary>
    internal void Close()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _window?.Close();
            _window = null;
        });
    }
}
