using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>The physical primary display and mode dimensions that bound refresh discovery.</summary>
internal sealed record DisplayOperatingPoint(DisplayTargetIdentity Target, int Width, int Height, int BitsPerPixel);
