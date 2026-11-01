namespace WSGM.Shell;

/// <summary>A published capability and the graphics plugin it came from, or null for the device package.</summary>
/// <param name="View">The capability.</param>
/// <param name="GpuPluginId">The graphics plugin's id, or null when the device package publishes it.</param>
internal sealed record PublishedCapability(DeviceCapabilityView View, string? GpuPluginId);
