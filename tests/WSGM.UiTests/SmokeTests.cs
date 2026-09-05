using Avalonia.Headless.XUnit;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class SmokeTests
{
    [AvaloniaFact]
    public void OverlayLoadsProductionResources()
    {
        using SystemStatus status = new();
        OverlayWindow window = new(new OverlayViewModel(), new AppSwitcherViewModel(), status,
            new OverlayWindow.SessionState(), w => { w.Width = 1280; w.Height = 650; }, _ => { });
        try { window.Show(); Assert.True(window.IsVisible); }
        finally { window.Close(); }
    }
}
