using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

internal sealed record DeviceCommandDispatch(
    CapabilityCommandResult Immediate,
    Task<CapabilityCommandResult>? LateCompletion = null);
