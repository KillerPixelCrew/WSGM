using System.Text.Json.Serialization;

namespace WSGM.Shell;

/// <summary>The snapshot's wire format, shared by the server and the client.</summary>
[JsonSerializable(typeof(DeviceCoordinatorDiagnosticsSnapshot))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class DeviceCoordinatorDiagnosticsJsonContext : JsonSerializerContext;
