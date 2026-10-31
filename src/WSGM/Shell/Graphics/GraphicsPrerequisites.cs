using System;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Graphics settings a driver is given together with another one, and the order they are written in.</summary>
/// <remarks>
///     AMD Fluid Motion Frames is turned on together with Radeon Anti-Lag on the same GPU, as Handheld
///     Companion does: it added Anti-Lag support solely to enable it with AFMF, and AFMF works there on the
///     devices where WSGM's AFMF write alone was refused with ADLX_FAIL. AMD recommends the pair. Anti-Lag goes
///     first, both from a user's edit and in a profile pass, and stays on when AFMF is turned off again, as it
///     does in Handheld Companion.
/// </remarks>
internal static class GraphicsPrerequisites
{
    /// <summary>AMD Fluid Motion Frames.</summary>
    internal const string Afmf = "graphics.afmf";

    /// <summary>Radeon Anti-Lag.</summary>
    internal const string AntiLag = "graphics.anti-lag";

    /// <summary>The value a prerequisite is written with.</summary>
    internal static CapabilityValue On => CapabilityValue.Boolean(true);

    /// <summary>The capability that must be on before this value is written, or null.</summary>
    /// <param name="capabilityId">The capability the user is changing.</param>
    /// <param name="value">The value the user set.</param>
    /// <returns><see cref="AntiLag" /> when AFMF is being turned on; otherwise null.</returns>
    internal static string? Required(string capabilityId, CapabilityValue value)
    {
        return string.Equals(capabilityId, Afmf, StringComparison.Ordinal) && value.BooleanValue == true
            ? AntiLag
            : null;
    }

    /// <summary>Orders a profile pass so a prerequisite is written before what depends on it.</summary>
    /// <param name="view">A capability the pass considers.</param>
    /// <returns>A lower value for a prerequisite.</returns>
    internal static int Priority(DeviceCapabilityView view)
    {
        return string.Equals(view.Descriptor.CapabilityId, AntiLag, StringComparison.Ordinal) ? -1 : 0;
    }
}
