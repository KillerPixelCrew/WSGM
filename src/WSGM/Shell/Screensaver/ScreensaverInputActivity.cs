using System;
using System.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Projects the existing Windows last-input producer through the Steam host's publication loop.</summary>
/// <remarks>
///     Steam's side extrapolates the age from when it received a sample and drops the sample 5.1 s
///     after the last one, so between inputs the same sample is answered again for up to
///     <see cref="Refresh" />. A fresh age on every publication round made every round deliver this
///     state into Steam's context, over once a second at idle.
/// </remarks>
internal sealed class ScreensaverInputActivity
{
    /// <summary>How long an unchanged sample is answered again; well inside Steam's 5.1 s lease.</summary>
    internal const uint Refresh = 3000;

    private readonly Lock _gate = new();
    private uint? _lastTick;
    private SteamScreensaverActivity? _published;
    private uint _publishedAt;
    private long _revision;

    internal SteamScreensaverActivity? Read()
    {
        var now = unchecked((uint)Environment.TickCount);
        var input = Observe(LastInput.Tick(), now);
        if (input is null)
        {
            lock (_gate)
            {
                _published = null;
            }

            return null;
        }

        lock (_gate)
        {
            if (_published is { } held && held.Revision == input.Revision && unchecked(now - _publishedAt) < Refresh)
            {
                return held;
            }
        }

        var sample = input with { OnBattery = PowerSource.ReadOnAc() is { } onAc ? !onAc : null };
        lock (_gate)
        {
            _published = sample;
            _publishedAt = now;
        }

        return sample;
    }

    /// <summary>Observes one successful session input sample, preserving wrap and query-failure semantics.</summary>
    internal SteamScreensaverActivity? Observe(uint? lastInputTick, uint now)
    {
        if (lastInputTick is not { } tick)
        {
            return null;
        }

        lock (_gate)
        {
            if (_lastTick != tick)
            {
                _lastTick = tick;
                _revision++;
            }

            return new SteamScreensaverActivity(_revision, unchecked(now - tick));
        }
    }
}
