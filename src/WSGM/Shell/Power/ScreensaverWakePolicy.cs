namespace WSGM.Shell;

/// <summary>Decides when a wake should dismiss Steam's screensaver: once per physical wake.</summary>
/// <remarks>
///     <para>
///         Steam ends its saver only when it sees input. After a wake nothing has reached it yet, so a
///         saver that was showing when the display went dark can stay up over a black screen. The
///         Atlas 8's wake on 2026-11-07 09:37:57 was a display-off wake with no system suspend at all,
///         so both a system sleep and a dark display arm the policy.
///     </para>
///     <para>
///         One physical wake can bring both a resume and a display-on, in either order, and a modern
///         standby wake brings a resume, a stale suspend and a second resume. Only the first waking
///         signal after the machine went dark asks for a dismissal; the stale suspend is never accepted,
///         so it does not arm the policy again.
///     </para>
/// </remarks>
internal sealed class ScreensaverWakePolicy
{
    private readonly object _gate = new();
    private bool _dark;

    /// <summary>A system suspend that was accepted as a real sleep.</summary>
    internal void NoteSuspendAccepted()
    {
        lock (_gate)
        {
            _dark = true;
        }
    }

    /// <summary>The display went dark.</summary>
    internal void NoteDisplayOff()
    {
        lock (_gate)
        {
            _dark = true;
        }
    }

    /// <summary>The system resumed.</summary>
    /// <returns>True when this wake should dismiss Steam's screensaver.</returns>
    internal bool NoteResume()
    {
        return Wake();
    }

    /// <summary>The display lit again.</summary>
    /// <returns>True when this wake should dismiss Steam's screensaver.</returns>
    internal bool NoteDisplayOn()
    {
        return Wake();
    }

    private bool Wake()
    {
        lock (_gate)
        {
            var wake = _dark;
            _dark = false;
            return wake;
        }
    }
}
