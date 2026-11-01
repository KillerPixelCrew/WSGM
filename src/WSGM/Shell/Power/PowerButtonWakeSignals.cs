using System;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Feeds the power button the session's display state and resumes, for its wake guard.</summary>
/// <remarks>
///     A press while this session's display is dark, or just after it lights or the system resumes,
///     belongs to the wake and must not run an action; a resume also tells Steam to show its resume
///     screen after a Steam suspend.
/// </remarks>
internal sealed class PowerButtonWakeSignals : IDisposable
{
    private readonly MessageWindow _messages;
    private readonly PowerButtonCoordinator _button;
    private bool _disposed;

    /// <param name="messages">The session's message window; not owned.</param>
    /// <param name="button">The device's power button; not owned.</param>
    internal PowerButtonWakeSignals(MessageWindow messages, PowerButtonCoordinator button)
    {
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _button = button ?? throw new ArgumentNullException(nameof(button));
        _messages.DisplayStateChanged += OnDisplayStateChanged;
        _messages.SystemResumed += _button.NoteResume;
        _messages.RegisterDisplayStateNotifications();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _messages.DisplayStateChanged -= OnDisplayStateChanged;
        _messages.SystemResumed -= _button.NoteResume;
        _messages.DeregisterDisplayStateNotifications();
    }

    private void OnDisplayStateChanged(int state, DisplayStateSource source)
    {
        var off = DisplayPowerSignal.IsDisplayOff(state);
        if (off && !DisplayPowerSignal.MayReportDark(source))
        {
            return;
        }

        _button.NoteDisplay(off);
    }
}
