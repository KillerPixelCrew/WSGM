using WSGM.Interop;

namespace WSGM.Core;

/// <summary>
///     The Windows power policy owners of one composition, built around a single <see cref="PowerSchemes" />
///     so scheme selection, timeouts, processor boost, core placement and the power mode share its
///     mutation lock.
/// </summary>
/// <remarks>
///     The session creates one and passes the owners its consumers need. Two instances never serialize
///     against each other, so a second one is only for a surface that writes nothing, such as the
///     Settings preview sheet.
/// </remarks>
internal sealed class WindowsPowerPolicy
{
    /// <summary>Creates the owners over the given Windows ports.</summary>
    /// <param name="schemes">Scheme enumeration, activation and policy values.</param>
    /// <param name="cpuBoost">Processor performance boost mode.</param>
    /// <param name="hybridCores">Hybrid processor core placement.</param>
    /// <param name="modes">The power-mode overlay.</param>
    internal WindowsPowerPolicy(IPowerSchemeApi schemes, ICpuBoostApi cpuBoost, IHybridCoreApi hybridCores,
        IPowerModeApi modes)
    {
        Schemes = new PowerSchemes(schemes);
        Timeouts = new PowerTimeouts(Schemes);
        CpuBoost = new CpuBoost(Schemes, cpuBoost);
        HybridCores = new HybridCores(Schemes, hybridCores);
        Modes = new WindowsPowerModes(Schemes, modes);
    }

    /// <summary>The scheme owner and the mutation lock every other owner here takes.</summary>
    internal PowerSchemes Schemes { get; }

    /// <summary>The display-off and standby idle timeouts.</summary>
    internal PowerTimeouts Timeouts { get; }

    /// <summary>The processor performance boost mode.</summary>
    internal CpuBoost CpuBoost { get; }

    /// <summary>The hybrid processor core placement.</summary>
    internal HybridCores HybridCores { get; }

    /// <summary>The Windows power-mode overlay.</summary>
    internal WindowsPowerModes Modes { get; }

    /// <summary>Creates the owners over the real Windows power APIs.</summary>
    /// <returns>A new set; the caller owns it for its composition.</returns>
    internal static WindowsPowerPolicy OverWindows()
    {
        return new WindowsPowerPolicy(new WindowsPowerSchemeApi(), new WindowsCpuBoostApi(),
            new WindowsHybridCoreApi(), new WindowsPowerModeApi());
    }
}
