using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    internal void AttachScreensavers(ScreensaverService? service)
    {
        ScreensaversHost.Attach(service);
    }
}
