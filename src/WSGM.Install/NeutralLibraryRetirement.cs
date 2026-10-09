using System.Collections.Generic;

namespace WSGM.Install;

/// <summary>Former first-party package identities replaced by the native libraries.</summary>
public static class NeutralLibraryRetirement
{
    /// <summary>Exact manifest IDs shared by setup, runtime discovery and development deployment.</summary>
    public static IReadOnlyList<string> PackageIds { get; } =
    [
        "wsgm.gpu.intel",
        "wsgm.gpu.amd",
        "wsgm.gpu.nvidia",
        "wsgm.device.msi.claw",
        "wsgm.device.msi.claw-8-a2vm",
        "wsgm.device.asus.rog-ally"
    ];
}
