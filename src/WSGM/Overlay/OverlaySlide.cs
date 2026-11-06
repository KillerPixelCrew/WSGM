using System;
using Avalonia.Threading;

namespace WSGM.Overlay;

/// <summary>
///     The slide-in both surface windows use: 180 ms with a cubic ease-out on a 16 ms UI-thread timer, applying
///     each eased progress through the owner's callback.
/// </summary>
internal sealed class OverlaySlide
{
    private const double DurationMs = 180;
    private readonly Action<double> _apply;
    private DateTime _startedUtc;
    private DispatcherTimer? _timer;

    /// <summary>Creates a slide that is not running.</summary>
    /// <param name="apply">Places the surface at an eased progress from 0 (start) to 1 (docked).</param>
    internal OverlaySlide(Action<double> apply)
    {
        _apply = apply;
    }

    /// <summary>Places the surface at its start and slides it in.</summary>
    internal void Start()
    {
        Stop();
        _apply(0);
        _startedUtc = DateTime.UtcNow;
        // Parameterless ctor + explicit Start: the 3-arg ctor auto-starts.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Stops the slide where it is.</summary>
    internal void Stop()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    /// <summary>The cubic ease-out: quick movement without a sharp stop.</summary>
    /// <param name="progress">Linear progress from 0 to 1.</param>
    /// <returns>The eased progress.</returns>
    internal static double Ease(double progress)
    {
        return 1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var progress = Math.Clamp((DateTime.UtcNow - _startedUtc).TotalMilliseconds / DurationMs, 0, 1);
        _apply(Ease(progress));
        if (progress >= 1)
        {
            Stop();
        }
    }
}
