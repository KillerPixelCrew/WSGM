using System;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Projects the existing Windows last-input producer through the Steam host's publication loop.</summary>
internal sealed class ScreensaverInputActivity
{
    private readonly System.Threading.Lock _gate = new();
    private uint? _lastTick;
    private long _revision;

    internal SteamScreensaverActivity? Read()
    {
        var input = Observe(LastInput.Tick(), unchecked((uint)Environment.TickCount));
        if (input is null)
        {
            return null;
        }

        return input with { OnBattery = PowerSource.ReadOnAc() is { } onAc ? !onAc : null };
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
