using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Input;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Device.Contracts.Ipc;

/// <summary>NativeAOT-safe JSON metadata for the low-rate semantic control plane.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(DeviceHostHello))]
[JsonSerializable(typeof(DeviceHostHelloAck))]
[JsonSerializable(typeof(DeviceActivateRequest))]
[JsonSerializable(typeof(DeviceLifecycleRequest))]
[JsonSerializable(typeof(DeviceDeactivateRequest))]
[JsonSerializable(typeof(DeviceLifecycleNotification))]
[JsonSerializable(typeof(DeviceResourceStateNotification))]
[JsonSerializable(typeof(DevicePhysicalIdentitiesNotification))]
[JsonSerializable(typeof(DeviceOemControlsNotification))]
[JsonSerializable(typeof(DeviceCancelCommandRequest))]
[JsonSerializable(typeof(DeviceControllerHandoffRequest))]
[JsonSerializable(typeof(DeviceControllerManagementRequest))]
[JsonSerializable(typeof(DeviceControllerHandoffResponse))]
[JsonSerializable(typeof(DeviceDiagnosticsRequest))]
[JsonSerializable(typeof(DeviceOperationAck))]
[JsonSerializable(typeof(DeviceProtocolError))]
[JsonSerializable(typeof(CapabilityDescriptorSet))]
[JsonSerializable(typeof(CapabilityState))]
[JsonSerializable(typeof(CapabilityStateDelta))]
[JsonSerializable(typeof(CapabilityCommand))]
[JsonSerializable(typeof(CapabilityCommandResult))]
[JsonSerializable(typeof(OemControlEvent))]
[JsonSerializable(typeof(HapticOutputFrame))]
[JsonSerializable(typeof(DeviceDiagnosticsSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<PhysicalDeviceIdentity>))]
[JsonSerializable(typeof(IReadOnlyList<OemControlDescriptor>))]
public sealed partial class DeviceWireJsonContext : JsonSerializerContext;
