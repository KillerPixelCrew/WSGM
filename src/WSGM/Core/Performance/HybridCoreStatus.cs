using System.Collections.Generic;

namespace WSGM.Core;

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
