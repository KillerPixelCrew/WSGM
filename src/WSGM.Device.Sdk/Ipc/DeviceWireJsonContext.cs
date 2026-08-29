using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Packaging;

namespace WSGM.Device.Sdk.Ipc;

/// <summary>NativeAOT-safe JSON metadata for the low-rate semantic control plane.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(DeviceHostHello))]
[JsonSerializable(typeof(DeviceHostHelloAck))]
[JsonSerializable(typeof(DeviceStartRequest))]
[JsonSerializable(typeof(DeviceLifecycleRequest))]
[JsonSerializable(typeof(DeviceStopRequest))]
[JsonSerializable(typeof(DeviceLifecycleNotification))]
[JsonSerializable(typeof(DevicePhysicalIdentitiesNotification))]
[JsonSerializable(typeof(DeviceOemControlsNotification))]
[JsonSerializable(typeof(DeviceCancelCommandRequest))]
[JsonSerializable(typeof(DeviceControllerHandoffRequest))]
[JsonSerializable(typeof(DeviceControllerManagementRequest))]
[JsonSerializable(typeof(DeviceControllerHandoffResponse))]
[JsonSerializable(typeof(DeviceDiagnosticsRequest))]
[JsonSerializable(typeof(DeviceTraceMessage))]
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
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(GlyphProfileManifest))]
public sealed partial class DeviceWireJsonContext : JsonSerializerContext;
