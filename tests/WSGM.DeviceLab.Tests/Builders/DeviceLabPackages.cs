using WSGM.DeviceLab.Preflight;

namespace WSGM.Device.Tests;

/// <summary>Package workflow inputs shared by the packaging and plugin workflow tests.</summary>
internal static class DeviceLabPackages
{
    /// <summary>Boundaries whose live data directory is a never-created child of the temporary root.</summary>
    internal static DeviceLabPathBoundaries Boundaries(TemporaryDirectory temporary) => new()
    {
        LiveDataDirectory = temporary.GetPath("never-live-data"),
        BroadHomeDirectories = [],
    };
}
