using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class OverlayPowerActionTests
{
    [AvaloniaTheory]
    [InlineData("Standby", SessionPowerAction.Standby, false)]
    [InlineData("Hibernate", SessionPowerAction.Hibernate, false)]
    [InlineData("Restart", SessionPowerAction.Restart, true)]
    [InlineData("Shut down", SessionPowerAction.Shutdown, true)]
    [InlineData("Sign out", SessionPowerAction.SignOut, true)]
    public void PowerRowsDismissBeforeReportingOneConfirmedIntent(string title, SessionPowerAction action,
        bool confirmation)
    {
        using var fixture = new UiFixture();
        var window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.PowerActions));
        List<string> calls = [];
        window.Dismissed += () => calls.Add("dismiss");
        window.PowerActionRequested += requested => calls.Add(requested.ToString());
        var button = window.GetVisualDescendants().OfType<ActionButton>()
            .Single(row => row.IsEffectivelyVisible && row.Title == title);
        UiFixture.Click(window, button);
        if (confirmation)
        {
            Assert.Empty(calls);
            UiFixture.Click(window, button);
        }

        Assert.Equal(["dismiss", action.ToString()], calls);
    }
}
