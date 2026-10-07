using System;
using System.Collections.Generic;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>
///     Polls the active managed controller, or connected SDL3 controllers, on the UI thread while
///     enabled. Keeps SDL hotplug pumping and emits button edges with D-pad/stick auto-repeat.
/// </summary>
internal sealed class GamepadService : IUiButtonSource, IDisposable
{
    // Monotonic deadlines keep clock corrections and resume from suppressing direction repeats.
    private const long RepeatInitialMs = 400;
    private const long RepeatRateMs = 150;

    /// <summary>The pad id the managed controller is tracked under; SDL instance ids never reach it.</summary>
    private const uint ManagedPadId = uint.MaxValue;

    private const GamepadButtons DirectionMask = GamepadButtons.DPadUp | GamepadButtons.DPadDown |
                                                 GamepadButtons.DPadLeft | GamepadButtons.DPadRight;

    private static readonly (GamepadButtons Flag, string Name)[] ButtonNames =
    [
        (GamepadButtons.A, "A"), (GamepadButtons.B, "B"), (GamepadButtons.X, "X"), (GamepadButtons.Y, "Y"),
        (GamepadButtons.LeftShoulder, "LB"), (GamepadButtons.RightShoulder, "RB"),
        (GamepadButtons.LeftThumb, "L3"), (GamepadButtons.RightThumb, "R3"),
        (GamepadButtons.Start, "Start"), (GamepadButtons.Back, "Back"),
        (GamepadButtons.DPadUp, "D-Up"), (GamepadButtons.DPadDown, "D-Down"),
        (GamepadButtons.DPadLeft, "D-Left"), (GamepadButtons.DPadRight, "D-Right"),
        (GamepadButtons.LeftTrigger, "L2"), (GamepadButtons.RightTrigger, "R2"),
        (GamepadButtons.L4, "L4"), (GamepadButtons.R4, "R4"),
        (GamepadButtons.L5, "L5"), (GamepadButtons.R5, "R5"),
        (GamepadButtons.Steam, "Steam"), (GamepadButtons.QuickAccess, "Quick Access"),
        (GamepadButtons.LeftPadPress, "L-Pad"), (GamepadButtons.RightPadPress, "R-Pad")
    ];

    private readonly List<SdlGamepads.PadSnapshot> _managedPads = [];

    /// <summary>
    ///     Last observed state per pad id. Edges and chords are evaluated per
    ///     pad so one controller holding a button cannot mask or complete another's.
    /// </summary>
    private readonly Dictionary<uint, GamepadButtons> _perPad = new();

    private readonly List<uint> _stalePads = [];

    private readonly DispatcherTimer _timer;
    private bool _loggedFirstPress;
    private ManagedUiPad? _managed;
    private long _nextRepeat;
    private GamepadButtons _repeating;

    /// <summary>Creates an inactive UI-thread polling service.</summary>
    public GamepadService()
    {
        // The callback constructor auto-starts; this service must remain inactive until Start.
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Gets whether the UI-thread polling timer is active.</summary>
    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Stops the UI-thread timer without disposing shared SDL state or emitting synthetic releases.</summary>
    public void Dispose()
    {
        _timer.Stop();
    }

    /// <summary>
    ///     Newly pressed buttons across all pads (edge-triggered per pad),
    ///     with auto-repeat for directions.
    /// </summary>
    public event Action<GamepadButtons>? ButtonPressed;

    /// <summary>
    ///     One pad's full button state, raised whenever it changes. Chord
    ///     detection needs the whole state per physical pad, not just the new edges.
    /// </summary>
    public event Action<uint, GamepadButtons>? StateChanged;

    /// <summary>
    ///     Initializes SDL, clears stale controller state, and begins polling.
    ///     A no-op while already polling.
    /// </summary>
    public void Start()
    {
        if (_timer.IsEnabled)
        {
            // Preserve held state so opening another surface cannot manufacture fresh press edges.
            return;
        }

        _perPad.Clear();
        _repeating = 0;
        _loggedFirstPress = false;
        SdlGamepads.EnsureInitialized();
        _timer.Start();
        Log.Info("Gamepad polling started.");
    }

    /// <summary>Stops polling on the UI thread without shutting down SDL or emitting releases; Start resets the baseline.</summary>
    public void Stop()
    {
        _timer.Stop();
    }

    /// <summary>Adds the managed controller, which replaces the SDL pads while management is active.</summary>
    /// <param name="pad">Borrowed thread-safe sample projection; switching source takes effect on the next UI poll.</param>
    internal void UseManagedPad(ManagedUiPad pad)
    {
        _managed = pad;
    }

    private void Poll()
    {
        var pads = SdlGamepads.Update();
        if (_managed is { IsActive: true } managed)
        {
            // The SDL pads leave through the stale-pad release below, so a chord in progress on one
            // cannot stay held. SDL is still pumped, for hotplug.
            _managedPads.Clear();
            _managedPads.Add(new SdlGamepads.PadSnapshot(ManagedPadId, managed.Buttons));
            pads = _managedPads;
        }

        GamepadButtons current = 0;
        GamepadButtons pressed = 0;
        foreach (var pad in pads)
        {
            current |= pad.Buttons;
            _perPad.TryGetValue(pad.Id, out var previous);
            // Edge-trigger per pad: pad A holding a button must not mask pad B
            // freshly pressing the same button.
            pressed |= pad.Buttons & ~previous;
            if (pad.Buttons == previous)
            {
                continue;
            }

            _perPad[pad.Id] = pad.Buttons;
            StateChanged?.Invoke(pad.Id, pad.Buttons);
        }

        // A pad unplugged mid-chord counts as a full release, so its chord state
        // downstream can't stay stuck holding phantom buttons.
        _stalePads.Clear();
        foreach (var (id, _) in _perPad)
        {
            var present = false;
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var pad in pads)
            {
                if (pad.Id != id)
                {
                    continue;
                }

                present = true;
                break;
            }

            if (!present)
            {
                _stalePads.Add(id);
            }
        }

        foreach (var id in _stalePads)
        {
            var previous = _perPad[id];
            _perPad.Remove(id);
            if (previous != 0)
            {
                StateChanged?.Invoke(id, 0);
            }
        }

        if (pressed != 0)
        {
            if (!_loggedFirstPress)
            {
                // One line per Start() so a pasted log proves input arrives at all.
                _loggedFirstPress = true;
                Log.Info($"Controller input: {Describe(pressed, false)}");
            }

            ButtonPressed?.Invoke(pressed);
        }

        // Auto-repeat for held directions (any pad).
        var directions = current & DirectionMask;
        if (directions != 0)
        {
            var newDirections = pressed & DirectionMask;
            if (newDirections != 0)
            {
                // A fresh press re-arms the repeat and becomes the repeated
                // direction, so a diagonal repeats the direction that initiated it
                // instead of the whole held set (which navigation resolves as Next).
                _repeating = newDirections;
                _nextRepeat = Environment.TickCount64 + RepeatInitialMs;
            }
            else if ((directions & _repeating) == 0)
            {
                // The repeated direction was released but another is still held
                // (diagonal released in the other order): re-arm on what remains.
                _repeating = directions;
                _nextRepeat = Environment.TickCount64 + RepeatInitialMs;
            }
            else if (Environment.TickCount64 >= _nextRepeat)
            {
                _nextRepeat = Environment.TickCount64 + RepeatRateMs;
                ButtonPressed?.Invoke(directions & _repeating);
            }
        }
        else
        {
            _repeating = 0;
        }
    }

    /// <summary>Formats a button combination for display, e.g. "Hold LB + Start".</summary>
    /// <param name="buttons">The buttons to render.</param>
    /// <param name="hold">Whether to prefix the result with <c>Hold</c>.</param>
    /// <returns>A user-facing chord description, or <c>None</c> for no buttons.</returns>
    public static string Describe(GamepadButtons buttons, bool hold)
    {
        if (buttons == 0)
        {
            return "None";
        }

        var names = new List<string>();
        foreach (var (flag, name) in ButtonNames)
        {
            if (buttons.HasFlag(flag))
            {
                names.Add(name);
            }
        }

        var combo = string.Join(" + ", names);
        return hold ? $"Hold {combo}" : combo;
    }
}
