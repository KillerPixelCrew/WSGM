using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class WindowFinderTests
{
    [Theory]
    [InlineData(true, false, 0, false, 0u, 5, true)]
    [InlineData(false, false, 0, false, 0u, 5, false)] // invisible
    [InlineData(true, true, 0, false, 0u, 5, false)] // Progman
    [InlineData(true, false, 0x0080, false, 0u, 5, false)] // WS_EX_TOOLWINDOW
    [InlineData(true, false, 0x0088, false, 0u, 5, false)] // tool window among other ex bits
    [InlineData(true, false, 0, true, 0u, 5, false)] // our own window
    [InlineData(true, false, 0, false, 2u, 5, false)] // DWM-cloaked UWP ghost
    [InlineData(true, false, 0, false, 0u, 0, false)] // untitled
    public void SwitchableWindowFilterAdmitsOnlyAltTabStyleWindows(
        bool isVisible, bool isShellWindow, int exStyle, bool isOwnProcess, uint cloaked, int titleLength, bool expected)
        => Assert.Equal(
            expected,
            WindowFinder.PassesSwitchableFilter(isVisible, isShellWindow, exStyle, isOwnProcess, cloaked, titleLength));

    [Fact]
    public void WindowSnapshotCarriesTheMinimizedStateForSwitcherPresentation()
    {
        var window = new WindowFinder.AppWindow(456, "Game", 789) { IsMinimized = true };

        Assert.True(window.IsMinimized);
        Assert.False(new WindowFinder.AppWindow(1, "A", 2).IsMinimized);
    }

    [Fact]
    public void RegistryAndWindowSnapshotsRetainTheirPositionalRecordContracts()
    {
        var uac = new UacSettings.UacState(true, 0, 1, 1);
        var window = new WindowFinder.AppWindow(456, "Game", 789);

        var (readable, consentPrompt, secureDesktop, enableLua) = uac;
        var (hwnd, title, processId) = window;

        Assert.True(readable);
        Assert.Equal(0, consentPrompt);
        Assert.Equal(1, secureDesktop);
        Assert.Equal(1, enableLua);
        Assert.Equal(456, hwnd);
        Assert.Equal("Game", title);
        Assert.Equal(789u, processId);
    }
}
