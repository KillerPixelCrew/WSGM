using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Borrowed device-package output sink; the session owns acquisition, capabilities and final release.</summary>
internal interface IPhysicalHapticSink
{
    /// <summary>Whether the session currently permits physical output; routers must stop sending after ownership is lost.</summary>
    bool IsOwned { get; }

    /// <summary>Current supported channels, motor floor, pulse duration and rate constraints.</summary>
    HapticCapabilities Capabilities { get; }

    /// <summary>Dispatches a canonical output frame, including silence to stop latched motors.</summary>
    /// <param name="frame">Frame already clamped and paced by the output router.</param>
    /// <param name="cancellationToken">Cooperative cancellation for the owning device operation.</param>
    /// <returns>Completion of dispatch; failures propagate to the router and do not imply the motors stopped.</returns>
    Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken);
}
