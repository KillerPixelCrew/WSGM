using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamBrowserSurfaceTests
{
    [Fact]
    public void NativeBrowserEntryRegistersNoPrivilegedWebsiteCommandChannel()
    {
        var module = SteamBrowserSurface.Module(() => true);

        Assert.Empty(module.Commands);
        Assert.Single(module.Publications);
        Assert.Single(module.Patches);
    }
}
