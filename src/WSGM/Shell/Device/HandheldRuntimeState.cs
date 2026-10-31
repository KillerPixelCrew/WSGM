using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

internal sealed record HandheldRuntimeState(
    DeviceCycleState State,
    string? DeviceDefinitionId,
    CapabilityReason? Reason);
