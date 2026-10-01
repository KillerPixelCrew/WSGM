using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

internal interface IPhysicalHapticSink
{
    bool IsOwned { get; }

    HapticCapabilities Capabilities { get; }

    Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken);
}
