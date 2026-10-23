using WSGM.Browser;
using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class BrowserPolicyTests
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("steam://run/480")]
    [InlineData("data:text/html,unsafe")]
    [InlineData("ms-settings:display")]
    [InlineData("https://name:password@example.com/")]
    public void PrivilegedAndCredentialBearingAddressesCannotBeNavigated(string address)
    {
        Assert.False(BrowserNavigation.IsAllowed(address));
        Assert.Throws<ArgumentException>(() => BrowserNavigation.Resolve(address));
    }

    [Theory]
    [InlineData("https://example.com/guide")]
    [InlineData("http://127.0.0.1:12345/guide")]
    public void HttpLookupsStayInTheIsolatedBrowser(string address)
    {
        Assert.True(BrowserNavigation.IsAllowed(address));
        Assert.Equal(address, BrowserNavigation.Resolve(address));
    }

    [Fact]
    public void GameIdentityOnlyAppearsInAnExplicitGameSearch()
    {
        const string query = "shield & poison";
        var ordinary = BrowserNavigation.Resolve(query);
        var withGame = BrowserNavigation.Resolve(query, "Example Game");

        Assert.Equal(BrowserNavigation.SearchPrefix + Uri.EscapeDataString(query), ordinary);
        Assert.Equal(BrowserNavigation.SearchPrefix + Uri.EscapeDataString("Example Game " + query), withGame);
    }

    [Fact]
    public void EncodedSearchCannotSilentlyExceedTheNavigationLimit()
    {
        Assert.Throws<ArgumentException>(() => BrowserNavigation.Resolve(new string('界', 1000)));
    }

    [Fact]
    public void ReloadedOptionsAreDetachedAndInvalidValuesAreNormalized()
    {
        var saved = new BrowserOptions { MemoryBehavior = (BrowserMemoryBehavior)77, ZoomFactor = double.NaN };
        var applied = saved.Normalize();
        saved.MemoryBehavior = BrowserMemoryBehavior.KeepAlive;
        saved.ZoomFactor = 3;

        Assert.Equal(BrowserMemoryBehavior.UnloadWhenClosed, applied.MemoryBehavior);
        Assert.Equal(1, applied.ZoomFactor);
    }
}
