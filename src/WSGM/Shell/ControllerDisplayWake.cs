using System;
using WSGM.Core;
using WSGM.Interop;
using CanonicalButtons = LibHandheld.Contracts.CanonicalButtons;
using CanonicalControllerSample = LibHandheld.Contracts.CanonicalControllerSample;

namespace WSGM.Shell;

/// <summary>Turns the display back on when a face button is pressed on the managed controller while it is dark.</summary>
/// <remarks>
///     Windows does not count gamepad input as activity, so a display the idle timeout switched off only
///     comes back for the power button, a keyboard or a mouse. While this session's display reports off, a
///     face-button press injects an empty mouse move, which Windows does count, and the press is withheld
///     from the game until released, so waking Big Picture does not also activate whatever has focus.
///     A locked session is out of reach: the device cycle is suspended and the lock screen does not accept
///     injected input.
/// </remarks>
internal sealed class ControllerDisplayWake : IDisposable
{
    private const CanonicalButtons FaceButtons =
        CanonicalButtons.A | CanonicalButtons.B | CanonicalButtons.X | CanonicalButtons.Y;

    private readonly ControllerManager _controllers;
    private readonly MessageWindow _messages;
    private volatile bool _dark;
    private bool _disposed;

    /// <summary>Only touched on the controller's single sample drain.</summary>
    private CanonicalButtons _lastFace;

    /// <summary>Subscribes to the session's display state and the managed controller's physical samples.</summary>
    /// <param name="messages">The session's message window; not owned or disposed here.</param>
    /// <param name="controllers">The device coordinator's controller manager; not owned here.</param>
    internal ControllerDisplayWake(MessageWindow messages, ControllerManager controllers)
    {
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _controllers = controllers ?? throw new ArgumentNullException(nameof(controllers));
        _messages.DisplayStateChanged += OnDisplayStateChanged;
        _messages.RegisterDisplayStateNotifications();
        _controllers.PhysicalSampleObserved += OnPhysicalSample;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controllers.PhysicalSampleObserved -= OnPhysicalSample;
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

        _dark = off;
    }

    private void OnPhysicalSample(CanonicalControllerSample sample)
    {
        var face = sample.Buttons & FaceButtons;
        var pressed = face & ~_lastFace;
        _lastFace = face;
        if (pressed is CanonicalButtons.None || !_dark)
        {
            return;
        }

        // Raised before the routing decision, so this very sample is already withheld.
        _controllers.WithholdUntilReleased(face);
        if (MouseInput.NudgeIdle())
        {
            Log.Change("display.controller-wake", "Display off: a controller face button woke the display.");
        }
        else
        {
            Log.Change("display.controller-wake",
                "Display off: a controller face button was pressed, but Windows refused the wake input.");
        }
    }
}
