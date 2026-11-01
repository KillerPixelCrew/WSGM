using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>What one configured step did.</summary>
/// <param name="Step">The configured step.</param>
/// <param name="Outcome">The plugin's own outcome.</param>
/// <param name="Detail">A bounded explanation suitable for the splash or the log.</param>
internal sealed record PluginActionStepResult(PluginActionStep Step, PluginActionOutcome Outcome, string Detail)
{
    /// <summary>Whether the step got far enough for the next one to run.</summary>
    internal bool Succeeded => Outcome is PluginActionOutcome.AppliedVerified or PluginActionOutcome.Dispatched;

    /// <summary>
    ///     Whether the step may have changed something despite not succeeding. Compensation
    ///     has to run for these too, because "unknown" is not "nothing happened".
    /// </summary>
    internal bool MayHaveActed => Outcome is not PluginActionOutcome.Rejected;
}
