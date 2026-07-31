using System;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Watches the controllers for the configured chord while the shell runs,
/// and fires once per matching press/hold. A chord only matches when it was pressed
/// on a single pad (per-pad tracking in ChordTracker).</summary>
public sealed class GamepadChordWatcher : IDisposable
{
    private readonly GamepadService _gamepad;
    private readonly ChordTracker _tracker;
    private GamepadChordConfig _config;

    public event Action? Triggered;

    public GamepadChordWatcher(GamepadService gamepad, GamepadChordConfig config)
    {
        _gamepad = gamepad;
        _config = config;
        _tracker = new ChordTracker();
        _tracker.HoldElapsed += OnHoldElapsed;
        _tracker.Released += OnReleased;
        _gamepad.StateChanged += OnStateChanged;
    }

    public void ApplyConfig(GamepadChordConfig config)
    {
        _config = config;
        _tracker.Reset();
    }

    private void OnStateChanged(uint padId, GamepadButtons state)
    {
        if (!_config.Enabled || _config.Buttons == 0)
        {
            return;
        }
        _tracker.OnState(padId, state);
    }

    private void OnHoldElapsed(ChordTracker.Pad pad)
    {
        if (!pad.HoldConsumed && _config.Hold && (int)pad.Union == _config.Buttons)
        {
            Fire();
            pad.HoldConsumed = true;    // don't repeat while still held
        }
    }

    private void OnReleased(ChordTracker.Pad pad)
    {
        // Full release: a press chord matches only if nothing extra was pressed on
        // that pad, and not when the same episode already fired as a hold.
        if (!pad.HoldConsumed && !_config.Hold && (int)pad.Union == _config.Buttons)
        {
            Fire();
        }
    }

    private void Fire()
    {
        Log.Info("Controller chord matched — opening overlay.");
        Triggered?.Invoke();
    }

    public void Dispose()
    {
        _gamepad.StateChanged -= OnStateChanged;
        _tracker.Dispose();
    }
}
