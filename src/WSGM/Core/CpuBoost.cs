using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Windows' processor performance boost mode, with HC's five choices.</summary>
/// <remarks>
///     The values are the ones Windows stores for <c>PERFBOOSTMODE</c> and the ones Handheld
///     Companion writes for its "CPU boost mode" (<c>CPUBoostLevel</c>). Windows also defines
///     aggressive-at-guaranteed modes 5 and 6; HC does not offer them, so neither does this.
/// </remarks>
public enum CpuBoostMode
{
    /// <summary>No boost: cores stay at their base frequency.</summary>
    Disabled = 0,

    /// <summary>Windows' default: boost when there is headroom.</summary>
    Enabled = 1,

    /// <summary>Boost as hard as the platform allows.</summary>
    Aggressive = 2,

    /// <summary>Enabled, but weighed against energy efficiency.</summary>
    EfficientEnabled = 3,

    /// <summary>Aggressive, but weighed against energy efficiency.</summary>
    EfficientAggressive = 4
}

/// <summary>One offered mode with the wording the user reads.</summary>
/// <param name="Mode">The mode this option selects.</param>
/// <param name="Name">Short label, HC's.</param>
/// <param name="Description">What it does for a game.</param>
internal sealed record CpuBoostOption(CpuBoostMode Mode, string Name, string Description);

/// <summary>What the machine reports and what is currently in effect.</summary>
/// <param name="Supported">Whether the active scheme exposes the setting at all.</param>
/// <param name="OnAc">The mode in effect while plugged in, or null when it is not one WSGM offers.</param>
/// <param name="OnBattery">The mode in effect on battery, or null when it is not one WSGM offers.</param>
internal sealed record CpuBoostStatus(bool Supported, CpuBoostMode? OnAc, CpuBoostMode? OnBattery);

/// <summary>
///     The processor boost mode over Windows power policy, written the way Handheld Companion
///     writes it: both power sources to one value on the active scheme, then the scheme re-activated.
/// </summary>
/// <remarks>
///     Independent of device integration: this is Windows power policy, not a device capability, so it
///     works with no plugin installed. Call from background work when projecting into a UI. HC
///     <c>PerformanceManager.RequestPerfBoostMode</c> and <c>PowerScheme.WritePowerCfg</c> are the
///     reference for the mechanism.
/// </remarks>
internal sealed class CpuBoost(ICpuBoostApi api)
{
    /// <summary>HC's five choices, in HC's order, with HC's labels.</summary>
    internal static readonly IReadOnlyList<CpuBoostOption> Offered =
    [
        new(CpuBoostMode.Disabled, "Disabled",
            "Cores stay at their base clock. Cooler and steadier; some games prefer it."),
        new(CpuBoostMode.Enabled, "Enabled",
            "Windows' default. Cores boost when there is thermal and power headroom."),
        new(CpuBoostMode.Aggressive, "Aggressive",
            "Boost as hard as the platform allows. Highest single-thread speed and draw."),
        new(CpuBoostMode.EfficientEnabled, "Efficient enabled",
            "Boost when there is headroom, weighed against energy efficiency."),
        new(CpuBoostMode.EfficientAggressive, "Efficient aggressive",
            "Boost hard, weighed against energy efficiency.")
    ];

    internal static CpuBoost Windows { get; } = new(new WindowsCpuBoostApi());

    /// <summary>The stable id a mode is published under, independent of its display name.</summary>
    /// <param name="mode">The mode to name.</param>
    /// <returns>An id matching the bridge's identifier rules.</returns>
    internal static string IdFor(CpuBoostMode mode)
    {
        return mode switch
        {
            CpuBoostMode.Disabled => "disabled",
            CpuBoostMode.Aggressive => "aggressive",
            CpuBoostMode.EfficientEnabled => "efficient-enabled",
            CpuBoostMode.EfficientAggressive => "efficient-aggressive",
            _ => "enabled"
        };
    }

    /// <summary>The mode an id names, or null when it names none.</summary>
    /// <param name="id">An id previously produced by <see cref="IdFor" />.</param>
    /// <returns>The mode, or null for an id this build does not know.</returns>
    internal static CpuBoostMode? ModeForId(string id)
    {
        foreach (var option in Offered)
        {
            if (string.Equals(IdFor(option.Mode), id, StringComparison.Ordinal))
            {
                return option.Mode;
            }
        }

        return null;
    }

    /// <summary>HC's label for a mode.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The label the user reads.</returns>
    internal static string NameFor(CpuBoostMode mode)
    {
        return Offered.FirstOrDefault(option => option.Mode == mode)?.Name ?? mode.ToString();
    }

    /// <summary>The mode a stored value reads back as, or null when it is none WSGM offers.</summary>
    /// <remarks>
    ///     Null rather than a nearest guess for modes 5 and 6: something else set them, and showing
    ///     them as one of WSGM's choices would claim WSGM put it there.
    /// </remarks>
    internal static CpuBoostMode? ModeFor(uint value)
    {
        return value <= (uint)CpuBoostMode.EfficientAggressive ? (CpuBoostMode)value : null;
    }

    /// <summary>Reads support and the effective modes.</summary>
    /// <returns>What is in effect for each power source, or unsupported when the scheme has no such setting.</returns>
    internal CpuBoostStatus Read()
    {
        try
        {
            var scheme = api.ReadActiveScheme();
            return new CpuBoostStatus(true, ModeFor(api.Read(scheme, false)), ModeFor(api.Read(scheme, true)));
        }
        catch (Win32Exception)
        {
            // A scheme without the processor boost setting refuses the read. Nothing to offer then.
            return new CpuBoostStatus(false, null, null);
        }
    }

    /// <summary>Applies one mode to both power sources, as HC does, and confirms it by readback.</summary>
    /// <remarks>
    ///     HC's sequence (<c>PowerScheme.WritePowerCfg</c>): reveal the setting, write AC and DC, then
    ///     re-activate the active scheme so the processor policy takes effect. Activation is global,
    ///     so the write and the activation happen under <see cref="PowerSchemes.MutationGate" />
    ///     together, the gate scheme selection and the core preference take as well.
    /// </remarks>
    /// <param name="mode">The mode to apply.</param>
    /// <param name="cancellationToken">Cancels before the write starts.</param>
    /// <exception cref="InvalidOperationException">Windows did not report the mode back.</exception>
    internal void Apply(CpuBoostMode mode, CancellationToken cancellationToken = default)
    {
        var value = (uint)mode;
        lock (PowerSchemes.MutationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Read the active scheme inside the shared gate: a scheme switch between the read and
            // the write would land the mode in a scheme that is no longer active.
            var scheme = api.ReadActiveScheme();
            // HC's ReadPowerCfg returns both sources in one call; both are read before comparing.
            var acValue = api.Read(scheme, false);
            var dcValue = api.Read(scheme, true);
            if (acValue == value && dcValue == value)
            {
                return;
            }

            _ = api.TryReveal();
            api.Write(scheme, false, value);
            api.Write(scheme, true, value);
            api.RefreshActiveScheme();
            foreach (var onBattery in (bool[])[false, true])
            {
                if (api.Read(scheme, onBattery) != value)
                {
                    throw new InvalidOperationException(
                        "Windows did not confirm the processor boost mode. "
                        + $"Requested {NameFor(mode)}, and the {(onBattery ? "battery" : "plugged in")} "
                        + "value reads back as something else.");
                }
            }
        }
    }
}
