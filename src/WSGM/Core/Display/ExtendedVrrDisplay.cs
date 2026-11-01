using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>An active display whose reported EDID names the extended VRR panel.</summary>
/// <param name="Target">The display.</param>
/// <param name="Reported">The EDID Windows reports for it, an override's while one is loaded.</param>
internal sealed record ExtendedVrrDisplay(DisplayTargetIdentity Target, byte[] Reported);
