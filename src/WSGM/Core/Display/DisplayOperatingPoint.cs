using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>The physical primary display and mode dimensions that bound refresh discovery.</summary>
/// <param name="Target">Stable physical display identity used to reject stale mode discovery.</param>
/// <param name="Width">Current horizontal resolution in physical pixels.</param>
/// <param name="Height">Current vertical resolution in physical pixels.</param>
/// <param name="BitsPerPixel">Current pixel format depth used when enumerating compatible modes.</param>
internal sealed record DisplayOperatingPoint(DisplayTargetIdentity Target, int Width, int Height, int BitsPerPixel);
