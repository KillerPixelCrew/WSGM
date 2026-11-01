using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One registered screensaver source with what its folder held at the last scan.</summary>
/// <param name="Source">A detached copy of the source's configuration.</param>
/// <param name="Content">What the scan read from the source's folder.</param>
internal sealed record ScreensaverEntry(ScreensaverSourceConfig Source, ScreensaverContentResult Content);
