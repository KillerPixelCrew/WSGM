using System;
using Avalonia.Threading;
using OpenFSE.Core;

namespace OpenFSE.Input;

/// <summary>Shared chord timings, modelled on Handheld Companion's InputsManager:
/// buttons accumulate into a union that only clears on full release (so a combo does
/// not need frame-perfect presses), and a hold timer restarted on every state change
/// promotes the chord to "hold".</summary>
internal static class ChordTiming
{
    /// <summary>Time with no state change before a held chord counts as a hold.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(600);
    /// <summary>Time with no input at all before recording gives up.</summary>
    public static readonly TimeSpan RecordingExpiry = TimeSpan.FromSeconds(3);
}

/// <summary>Records a controller chord: press one or more buttons (in any order) and
/// either release them (press chord) or keep holding (hold chord).</summary>
public sealed class GamepadChordRecorder : IDisposable
{
    private readonly GamepadService _gamepad;
    private readonly bool _ownsService;
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _expiryTimer;
    private GamepadButtons _union;
    private bool _recording;

    /// <summary>(buttons, isHold). Empty buttons = cancelled/timed out.</summary>
    public event Action<GamepadButtons, bool>? Recorded;

    public GamepadChordRecorder(GamepadService? gamepad = null)
    {
        _ownsService = gamepad is null;
        _gamepad = gamepad ?? new GamepadService();

        _holdTimer = new DispatcherTimer { Interval = ChordTiming.Hold };
        _holdTimer.Tick += (_, _) => Finish(isHold: true);

        _expiryTimer = new DispatcherTimer { Interval = ChordTiming.RecordingExpiry };
        _expiryTimer.Tick += (_, _) => Finish(isHold: false, cancelled: _union == 0);
    }

    public void Start()
    {
        _union = 0;
        _recording = true;
        _gamepad.StateChanged += OnStateChanged;
        if (!_gamepad.IsRunning)
        {
            _gamepad.Start();
        }
        _expiryTimer.Start();
    }

    private void OnStateChanged(GamepadButtons state)
    {
        if (!_recording)
        {
            return;
        }

        // Every state change restarts both clocks: the hold timer measures "time since
        // the last change", which is what lets a second button join the combo late.
        _holdTimer.Stop();
        _expiryTimer.Stop();

        if (state != 0)
        {
            _union |= state;            // union, cleared only on full release
            _holdTimer.Start();
            _expiryTimer.Start();
            return;
        }

        // Everything released -> a press chord.
        Finish(isHold: false);
    }

    private void Finish(bool isHold, bool cancelled = false)
    {
        if (!_recording)
        {
            return;
        }
        _recording = false;
        _holdTimer.Stop();
        _expiryTimer.Stop();
        _gamepad.StateChanged -= OnStateChanged;
        if (_ownsService)
        {
            _gamepad.Stop();
        }

        var buttons = cancelled ? 0 : _union;
        Log.Info($"Recorded controller chord: {GamepadService.Describe(buttons, isHold)}");
        Recorded?.Invoke(buttons, isHold && buttons != 0);
    }

    public void Cancel() => Finish(isHold: false, cancelled: true);

    public void Dispose()
    {
        _gamepad.StateChanged -= OnStateChanged;
        _holdTimer.Stop();
        _expiryTimer.Stop();
        if (_ownsService)
        {
            _gamepad.Dispose();
        }
    }
}

/// <summary>Watches the controller for the configured chord while the shell runs, and
/// fires once per matching press/hold.</summary>
public sealed class GamepadChordWatcher : IDisposable
{
    private readonly GamepadService _gamepad;
    private readonly DispatcherTimer _holdTimer;
    private GamepadChordConfig _config;
    private GamepadButtons _union;
    private bool _fired;

    public event Action? Triggered;

    public GamepadChordWatcher(GamepadService gamepad, GamepadChordConfig config)
    {
        _gamepad = gamepad;
        _config = config;
        _holdTimer = new DispatcherTimer { Interval = ChordTiming.Hold };
        _holdTimer.Tick += (_, _) => OnHoldElapsed();
        _gamepad.StateChanged += OnStateChanged;
    }

    public void ApplyConfig(GamepadChordConfig config)
    {
        _config = config;
        _union = 0;
        _fired = false;
        _holdTimer.Stop();
    }

    private void OnStateChanged(GamepadButtons state)
    {
        if (!_config.Enabled || _config.Buttons == 0)
        {
            return;
        }

        _holdTimer.Stop();

        if (state != 0)
        {
            _union |= state;
            _holdTimer.Start();
            return;
        }

        // Full release: a press chord matches only if nothing extra was pressed.
        if (!_fired && !_config.Hold && (int)_union == _config.Buttons)
        {
            Fire();
        }
        _union = 0;
        _fired = false;
    }

    private void OnHoldElapsed()
    {
        _holdTimer.Stop();
        if (!_fired && _config.Hold && (int)_union == _config.Buttons)
        {
            Fire();
            _fired = true;      // don't repeat while still held
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
        _holdTimer.Stop();
    }
}
