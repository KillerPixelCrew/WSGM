using WSGM.Settings;

namespace WSGM.Tests.Settings;

public sealed class SteamCefPluginConsentTests
{
    [Fact]
    public void ReloadRequiresConsentAndProducesExplicitIntent()
    {
        var acknowledged = false;
        CommonPluginInstanceRow row = new("example.frontend", "default", "Frontend", false, true,
            true, cefFailure: "page: render failed", cefAcknowledged: () => acknowledged);
        row.SteamCefEnabled = true;
        row.ReloadCefCommand.Execute(null);
        Assert.False(row.SteamCefEnabled);
        Assert.True(row.HasCefFailure);
        acknowledged = true;
        row.ReloadCefCommand.Execute(null);
        var captured = row.Capture();
        Assert.True(captured.Enabled);
        Assert.True(captured.SteamCefEnabled);
        Assert.True(captured.SteamCefReloadRequested);
        Assert.Null(captured.SteamCefFailure);
    }
}
