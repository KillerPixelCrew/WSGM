using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>What one publisher's reconciliation pass needs from its owner.</summary>
/// <param name="Label">The publisher's name in the log, such as "Device".</param>
/// <param name="Snapshot">Reads the router's current views.</param>
/// <param name="ReadCurrent">Reads one candidate fresh without rebuilding every view.</param>
/// <param name="ExecuteAsync">
///     Writes one admitted value and returns the result, or null when the owner skipped it on its own
///     terms, such as a lighting zone that used up its attempts.
/// </param>
internal sealed record CapabilityReconcilePass(
    string Label,
    Func<IReadOnlyList<DeviceCapabilityView>> Snapshot,
    Func<DeviceCapabilityKey, DeviceCapabilityView?> ReadCurrent,
    Func<DeviceCapabilityView, CapabilityValue, CancellationToken, Task<CapabilityCommandResult?>> ExecuteAsync)
{
    /// <summary>Orders the writes; lower values go first.</summary>
    public Func<DeviceCapabilityView, int> Priority { get; init; } = static _ => 0;

    /// <summary>Which capabilities this pass considers at all.</summary>
    public Func<DeviceCapabilityView, bool> Include { get; init; } = static _ => true;

    /// <summary>Checked before each capability; true abandons the rest of the pass.</summary>
    public Func<bool> Abandon { get; init; } = static () => false;

    /// <summary>
    ///     Whether the publisher has reported the capability at all. One that has not is waiting for its first
    ///     state rather than unavailable, so it is skipped without a warning.
    /// </summary>
    public Func<DeviceCapabilityView, bool> HasState { get; init; } = static _ => true;
}
