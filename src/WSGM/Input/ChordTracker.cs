using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace WSGM.Input;

/// <summary>
///     UI-thread chord tracker shared by recording and activation. Buttons accumulate per physical pad
///     until full release; a hold fires 600 ms after that pad's last state change.
/// </summary>
internal sealed class ChordTracker : IDisposable
{
    /// <summary>Time with no state change before a held chord counts as a hold.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(600);

    /// <summary>Time with no input at all before recording gives up.</summary>
    public static readonly TimeSpan RecordingExpiry = TimeSpan.FromSeconds(3);

    private readonly Dictionary<uint, Pad> _pads = new();

    /// <summary>Stops every hold timer and clears episodes without raising release or cancellation events.</summary>
    public void Dispose()
    {
        Reset();
    }

    /// <summary>
    ///     The hold time elapsed with no state change on this pad. Can fire
    ///     again after further state changes unless the consumer sets HoldConsumed.
    /// </summary>
    public event Action<Pad>? HoldElapsed;

    /// <summary>
    ///     The pad was fully released; Union is the accumulated press chord.
    ///     The pad's episode state resets after the handlers return.
    /// </summary>
    public event Action<Pad>? Released;

    /// <summary>Feeds one changed full button state on the UI thread.</summary>
    /// <param name="padId">Stable identity for the physical source; different sources never form a shared chord.</param>
    /// <param name="state">Complete held mask; zero completes and removes the episode, including on disconnect.</param>
    public void OnState(uint padId, GamepadButtons state)
    {
        if (!_pads.TryGetValue(padId, out var pad))
        {
            var newPad = new Pad();
            newPad.HoldTimer.Tick += (_, _) =>
            {
                newPad.HoldTimer.Stop();
                HoldElapsed?.Invoke(newPad);
            };
            _pads[padId] = pad = newPad;
        }

        // Restart on changes so another button can join the same hold episode.
        pad.HoldTimer.Stop();

        if (state != 0)
        {
            pad.Union |= state;
            pad.HoldTimer.Start();
            return;
        }

        Released?.Invoke(pad);
        pad.Union = 0;
        pad.HoldConsumed = false;
        // SDL allocates a new identity on replug; eviction prevents an abandoned timer per old identity.
        _pads.Remove(padId);
    }

    /// <summary>Stops timers and clears all episodes on the UI thread without notifying consumers.</summary>
    public void Reset()
    {
        foreach (var pad in _pads.Values)
        {
            pad.HoldTimer.Stop();
            pad.Union = 0;
            pad.HoldConsumed = false;
        }
    }

    /// <summary>One pad's chord episode. Union accumulates until full release.</summary>
    internal sealed class Pad
    {
        /// <summary>UI-thread timer restarted by each state change and stopped before release notification.</summary>
        public readonly DispatcherTimer HoldTimer = new() { Interval = Hold };

        /// <summary>
        ///     Set by a consumer that acted on HoldElapsed so it does not act
        ///     again (further state changes restart the hold timer) until full release.
        /// </summary>
        public bool HoldConsumed;

        /// <summary>Every button observed during this episode; inspect during Released before it resets.</summary>
        public GamepadButtons Union;
    }
}
