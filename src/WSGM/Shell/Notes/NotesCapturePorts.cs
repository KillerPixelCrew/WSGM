using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>The effects one screenshot transaction needs, injected so it can run without a live game.</summary>
/// <param name="Surface">Returns to the game and reopens Notes, for the origin surface.</param>
/// <param name="Countdown">The countdown display.</param>
/// <param name="Capture">Captures the selected application window or desktop display.</param>
/// <param name="IsAlive">Whether the game process still runs.</param>
/// <param name="Delay">Waits; replaced by tests.</param>
internal sealed record NotesCapturePorts(
    INotesCaptureSurface Surface,
    INotesCountdownDisplay Countdown,
    Func<NotesCaptureTarget, CancellationToken, Task<NotesCaptureFrame>> Capture,
    Func<uint, bool> IsAlive,
    Func<TimeSpan, CancellationToken, Task>? Delay = null);
