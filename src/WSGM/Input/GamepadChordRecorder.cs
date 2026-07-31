using System;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Records a controller chord: press one or more buttons (in any order) on
/// one pad and either release them (press chord) or keep holding (hold chord).</summary>
public sealed class GamepadChordRecorder : IDisposable
{
    private readonly GamepadService _gamepad;
    private readonly bool _ownsService;
    private readonly ChordTracker _tracker;
    private readonly DispatcherTimer _expiryTimer;
    private bool _recording;

    /// <summary>(buttons, isHold). Empty buttons = cancelled/timed out.</summary>
    public event Action<GamepadButtons, bool>? Recorded;

    public GamepadChordRecorder(GamepadService? gamepad = null)
    {
        _ownsService = gamepad is null;
        _gamepad = gamepad ?? new GamepadService();

        _tracker = new ChordTracker();
        _tracker.HoldElapsed += pad => Finish(pad.Union, isHold: true);
        _tracker.Released += pad => Finish(pad.Union, isHold: false);

        _expiryTimer = new DispatcherTimer { Interval = ChordTiming.RecordingExpiry };
        // The hold timer resolves any pressed buttons long before expiry and a full
        // release finishes immediately, so expiring can only mean no input at all.
        _expiryTimer.Tick += (_, _) => Finish(0, isHold: false, cancelled: true);
    }

    public void Start()
    {
        _tracker.Reset();
        _recording = true;
        // Unsubscribe first so a Start() without an intervening Finish() cannot
        // stack a second subscription.
        _gamepad.StateChanged -= OnStateChanged;
        _gamepad.StateChanged += OnStateChanged;
        if (!_gamepad.IsRunning)
        {
            _gamepad.Start();
        }
        _expiryTimer.Stop();
        _expiryTimer.Start();
    }

    private void OnStateChanged(uint padId, GamepadButtons state)
    {
        if (!_recording)
        {
            return;
        }
        // Any input restarts the give-up clock.
        _expiryTimer.Stop();
        _expiryTimer.Start();
        _tracker.OnState(padId, state);
    }

    private void Finish(GamepadButtons union, bool isHold, bool cancelled = false)
    {
        if (!_recording)
        {
            return;
        }
        _recording = false;
        _expiryTimer.Stop();
        _tracker.Reset();
        _gamepad.StateChanged -= OnStateChanged;
        if (_ownsService)
        {
            _gamepad.Stop();
        }

        var buttons = cancelled ? 0 : union;
        Log.Info($"Recorded controller chord: {GamepadService.Describe(buttons, isHold)}");
        Recorded?.Invoke(buttons, isHold && buttons != 0);
    }

    public void Cancel() => Finish(0, isHold: false, cancelled: true);

    public void Dispose()
    {
        _gamepad.StateChanged -= OnStateChanged;
        _expiryTimer.Stop();
        _tracker.Dispose();
        if (_ownsService)
        {
            _gamepad.Dispose();
        }
    }
}
