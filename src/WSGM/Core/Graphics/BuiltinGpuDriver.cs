using LibGPUDriverInteract;

namespace WSGM.Core;

/// <summary>A linked graphics backend and its stable profile identity.</summary>
internal sealed record BuiltinGpuDriver(GpuVendor Vendor, string Id, string Name);
