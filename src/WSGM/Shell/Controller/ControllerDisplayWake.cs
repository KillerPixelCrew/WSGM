using System;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Turns the display back on when a face button is pressed on the managed controller while it is dark.</summary>
/// <remarks>
///     Windows does not count gamepad input as activity, so a display the idle timeout switched off only
///     comes back for the power button, a keyboard or a mouse. While this session's display reports off, a
///     face-button press injects an empty mouse move, which Windows does count; <see cref="ControllerWakeGate" />
///     decides which press that is. The display-on notification still matters after a wake by other means, so
///     the next face button reaches the game. A locked session is out of reach: the device cycle is suspended
///     and the lock screen does not accept injected input.
/// </remarks>
internal sealed class ControllerDisplayWake : IDisposable
{
    private readonly ControllerForwarding _controllers;
    private readonly ControllerWakeGate _gate;
    private readonly MessageWindow _messages;
    private bool _disposed;

    /// <summary>Subscribes to the session's display state and the managed controller's physical samples.</summary>
    /// <param name="messages">The session's message window; not owned or disposed here.</param>
    /// <param name="controllers">The device coordinator's controller forwarding; not owned here.</param>
    internal ControllerDisplayWake(MessageWindow messages, ControllerForwarding controllers)
    {
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _controllers = controllers ?? throw new ArgumentNullException(nameof(controllers));
        _gate = new ControllerWakeGate(_controllers.WithholdUntilReleased, MouseInput.NudgeIdle);
        _messages.DisplayStateChanged += OnDisplayStateChanged;
        _messages.RegisterDisplayStateNotifications();
        _controllers.PhysicalSampleObserved += _gate.Observe;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controllers.PhysicalSampleObserved -= _gate.Observe;
        _messages.DisplayStateChanged -= OnDisplayStateChanged;
        _messages.DeregisterDisplayStateNotifications();
    }

    private void OnDisplayStateChanged(int state, DisplayStateSource source)
    {
        var off = DisplayPowerSignal.IsDisplayOff(state);
        if (off && !DisplayPowerSignal.MayReportDark(source))
        {
            return;
        }

        _gate.NoteDisplay(off);
    }
}
