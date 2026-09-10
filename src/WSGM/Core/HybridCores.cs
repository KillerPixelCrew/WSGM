using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>How WSGM offers to place threads across a hybrid CPU's core types.</summary>
/// <remarks>
/// These are WSGM's names for values Windows publishes for its thread scheduling policy. Windows
/// names them itself, which is why they are the ones WSGM writes: its heterogeneous-policy setting
/// is enumerated only as "use heterogeneous policy 0..4" with no published meaning, so WSGM reads
/// and restores that value but never chooses one.
/// </remarks>
internal enum HybridCoreMode
{
    /// <summary>Windows places threads. The state a machine ships in.</summary>
    Automatic,

    /// <summary>Both core types stay eligible; performance cores are preferred.</summary>
    PreferPerformance,

    /// <summary>Both core types stay eligible; efficiency cores are preferred.</summary>
    PreferEfficiency,

    /// <summary>Only performance cores are eligible.</summary>
    PerformanceOnly,

    /// <summary>Only efficiency cores are eligible.</summary>
    EfficiencyOnly,
}

/// <summary>One offered mode with the wording the user reads.</summary>
/// <param name="Mode">The mode this option selects.</param>
/// <param name="Name">Short label.</param>
/// <param name="Description">What it does, in terms of the machine rather than the scheduler.</param>
internal sealed record HybridCoreOption(HybridCoreMode Mode, string Name, string Description);

/// <summary>What the machine supports and what is currently in effect.</summary>
/// <param name="Supported">Whether the control should be offered at all.</param>
/// <param name="PerformanceCores">Cores in the most performant efficiency class.</param>
/// <param name="EfficiencyCores">Cores in every other class.</param>
/// <param name="Options">The modes this machine accepts, in offer order.</param>
/// <param name="OnAc">The mode in effect while plugged in, or null when it is not one WSGM offers.</param>
/// <param name="OnBattery">The mode in effect on battery, or null when it is not one WSGM offers.</param>
internal sealed record HybridCoreStatus(
    bool Supported,
    int PerformanceCores,
    int EfficiencyCores,
    IReadOnlyList<HybridCoreOption> Options,
    HybridCoreMode? OnAc,
    HybridCoreMode? OnBattery);

/// <summary>
/// Manual hybrid core placement over the reusable library. Reads always consult Windows; a write is
/// confirmed by readback and applied to the active scheme.
/// </summary>
/// <remarks>
/// Independent of device integration: this is Windows power policy, not a device capability, so it
/// works with no plugin installed. Call from background work when projecting into a UI.
/// </remarks>
internal sealed class HybridCores(IHybridCoreApi api)
{
    internal static HybridCores Windows { get; } = new(new WindowsHybridCoreApi());

    /// <summary>Windows applies processor policy on scheme activation, and activation is global.</summary>
    internal static object MutationGate { get; } = new();

    private static readonly HybridCoreOption[] Offered =
    [
        new(HybridCoreMode.Automatic, "Automatic",
            "Windows decides which cores run what. The setting your handheld shipped with."),
        new(HybridCoreMode.PreferPerformance, "Prefer performance cores",
            "Games get the fast cores first; background work still uses the efficient ones."),
        new(HybridCoreMode.PreferEfficiency, "Prefer efficiency cores",
            "Favours the low-power cores for longer battery life at lower frame rates."),
        new(HybridCoreMode.PerformanceOnly, "Performance cores only",
            "Nothing runs on the efficient cores. Highest draw, and fewer cores in total."),
        new(HybridCoreMode.EfficiencyOnly, "Efficiency cores only",
            "Nothing runs on the fast cores. Lowest draw, for light or idle sessions."),
    ];

    /// <summary>The scheduling policy pair a mode writes, for ordinary and short-running threads.</summary>
    /// <remarks>
    /// One pair, not two independent settings: a mode that steered ordinary threads one way and
    /// short-lived ones another would be a placement nobody asked for and could not be read back as
    /// any offered mode.
    /// </remarks>
    private static HybridSchedulingPolicy PolicyFor(HybridCoreMode mode) => mode switch
    {
        HybridCoreMode.PreferPerformance => HybridSchedulingPolicy.PreferPerformantProcessors,
        HybridCoreMode.PreferEfficiency => HybridSchedulingPolicy.PreferEfficientProcessors,
        HybridCoreMode.PerformanceOnly => HybridSchedulingPolicy.PerformantProcessors,
        HybridCoreMode.EfficiencyOnly => HybridSchedulingPolicy.EfficientProcessors,
        _ => HybridSchedulingPolicy.Automatic,
    };

    /// <summary>The mode a stored pair reads back as, or null when it matches none WSGM offers.</summary>
    /// <remarks>
    /// Null rather than a nearest guess. Something else set that pair — an OEM tool, a policy, a
    /// hand edit — and showing it as one of WSGM's modes would claim WSGM put it there.
    /// </remarks>
    internal static HybridCoreMode? ModeFor(HybridCoreState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Threads != state.ShortThreads)
        {
            return null;
        }
        foreach (HybridCoreOption option in Offered)
        {
            if (PolicyFor(option.Mode) == state.Threads)
            {
                return option.Mode;
            }
        }
        return null;
    }

    /// <summary>The stable id a mode is published under, independent of its display name.</summary>
    /// <remarks>
    /// One vocabulary for every surface. The overlay picks a mode from the list directly, but Steam
    /// carries the choice as a string over the bridge, and an id derived from the localized name
    /// would change meaning the moment the wording did.
    /// </remarks>
    /// <param name="mode">The mode to name.</param>
    /// <returns>An id matching the bridge's identifier rules.</returns>
    internal static string IdFor(HybridCoreMode mode) => mode switch
    {
        HybridCoreMode.PreferPerformance => "prefer-performance",
        HybridCoreMode.PreferEfficiency => "prefer-efficiency",
        HybridCoreMode.PerformanceOnly => "performance-only",
        HybridCoreMode.EfficiencyOnly => "efficiency-only",
        _ => "automatic",
    };

    /// <summary>The mode an id names, or null when it names none.</summary>
    /// <param name="id">An id previously produced by <see cref="IdFor"/>.</param>
    /// <returns>The mode, or null for an id this build does not know.</returns>
    internal static HybridCoreMode? ModeForId(string id)
    {
        foreach (HybridCoreOption option in Offered)
        {
            if (string.Equals(IdFor(option.Mode), id, StringComparison.Ordinal))
            {
                return option.Mode;
            }
        }
        return null;
    }

    /// <summary>Reads support and the effective modes. Native failures propagate.</summary>
    /// <returns>What to offer, and what is in effect for each power source.</returns>
    internal HybridCoreStatus Read()
    {
        Guid scheme = api.ReadActiveScheme();
        HybridCoreSupport support = api.Query(scheme);
        if (!support.Hybrid || !support.Configurable)
        {
            return new(false, 0, 0, [], null, null);
        }

        int performance = 0;
        int efficiency = 0;
        byte best = 0;
        foreach (HybridCoreClass observed in support.Classes)
        {
            best = Math.Max(best, observed.EfficiencyClass);
        }
        foreach (HybridCoreClass observed in support.Classes)
        {
            if (observed.EfficiencyClass == best) { performance += observed.Cores; }
            else { efficiency += observed.Cores; }
        }

        List<HybridCoreOption> options = [];
        foreach (HybridCoreOption option in Offered)
        {
            // Only modes this Windows build actually publishes a value for. A mode offered here that
            // the machine will not accept is a control that does nothing when pressed.
            if (support.SchedulingPolicies.Contains(PolicyFor(option.Mode))
                && support.ShortSchedulingPolicies.Contains(PolicyFor(option.Mode)))
            {
                options.Add(option);
            }
        }

        return new(
            options.Count > 0,
            performance,
            efficiency,
            options,
            ModeFor(api.Read(scheme, onBattery: false)),
            ModeFor(api.Read(scheme, onBattery: true)));
    }

    /// <summary>Applies one mode to both power sources and confirms it by readback.</summary>
    /// <remarks>
    /// The heterogeneous-policy value is carried through untouched, because Windows publishes no
    /// meaning for it. Activation is what makes processor policy take effect, and it is global, so
    /// the write and the activation happen under <see cref="MutationGate"/> together.
    /// </remarks>
    /// <param name="mode">The mode to apply.</param>
    /// <param name="cancellationToken">Cancels before the write starts.</param>
    /// <exception cref="InvalidOperationException">Windows did not report the mode back.</exception>
    internal void Apply(HybridCoreMode mode, CancellationToken cancellationToken = default)
    {
        Guid scheme = api.ReadActiveScheme();
        HybridSchedulingPolicy policy = PolicyFor(mode);
        lock (MutationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (bool onBattery in (bool[])[false, true])
            {
                HybridCoreState previous = api.Read(scheme, onBattery);
                api.Write(scheme, onBattery, previous with { Threads = policy, ShortThreads = policy });
            }

            api.RefreshActiveScheme();
            foreach (bool onBattery in (bool[])[false, true])
            {
                if (ModeFor(api.Read(scheme, onBattery)) != mode)
                {
                    throw new InvalidOperationException(
                        "Windows did not confirm the processor core preference. "
                            + $"Requested {mode}, and the {(onBattery ? "battery" : "plugged in")} "
                            + "value reads back as something else.");
                }
            }
        }
    }
}
