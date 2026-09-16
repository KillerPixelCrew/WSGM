using WSGM.Device.Tests;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Tests.Builders;

/// <summary>Package workflow inputs shared by the packaging and plugin workflow tests.</summary>
internal static class DeviceLabPackages
{
    /// <summary>Boundaries whose live data directory is a never-created child of the temporary root.</summary>
    internal static DeviceLabPathBoundaries Boundaries(TemporaryDirectory temporary)
    {
        return new DeviceLabPathBoundaries
        {
            LiveDataDirectory = temporary.GetPath("never-live-data"),
            BroadHomeDirectories = []
        };
    }
}
