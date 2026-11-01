using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What WSGM can honestly tell the user about the last standby.</summary>
/// <param name="Supported">Whether this machine does Modern Standby at all.</param>
/// <param name="Summary">One sentence for the settings surface.</param>
/// <param name="ArmedWakeSources">
///     The devices currently allowed to wake the machine, named as Windows names them. This is the one
///     genuinely actionable diagnostic here: Windows will not say what woke the machine, but it will
///     say what is permitted to, and on a handheld that list is usually the answer.
/// </param>
public sealed record ModernStandbyReport(
    bool Supported,
    string Summary,
    IReadOnlyList<string> ArmedWakeSources);
