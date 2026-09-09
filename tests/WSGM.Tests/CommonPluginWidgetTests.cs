using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class CommonPluginWidgetTests
{
    private static readonly PluginUiContribution Status = new("temperature", "Temperature", "device", PluginUiKind.Status, "temperature");

    [Fact]
    public void WidgetLinksAreCapturedWithoutRetainingMutablePluginLists()
    {
        List<string> links = ["temperature"];
        PluginWidget widget = new("thermal", "Thermals", links, NavigationCategory: "device");
        var captured = CommonPluginActions.CaptureWidgets([widget], [Status]);
        links[0] = "changed";
        Assert.Equal("temperature", Assert.Single(Assert.Single(captured).ContributionIds));
        Assert.Throws<ArgumentException>(() => CommonPluginActions.CaptureWidgets([widget], [Status]));
    }

    [Fact]
    public void DuplicateWidgetsAndUnknownNavigationAreRejected()
    {
        PluginWidget widget = new("thermal", "Thermals", ["temperature"]);
        Assert.Throws<ArgumentException>(() => CommonPluginActions.CaptureWidgets([widget, widget], [Status]));
        Assert.Throws<ArgumentException>(() => CommonPluginActions.CaptureWidgets(
            [widget with { NavigationCategory = "missing" }], [Status]));
        Assert.Throws<ArgumentException>(() => CommonPluginActions.CaptureWidgets(
            [widget with { ContributionIds = ["temperature", "temperature"] }], [Status]));
    }
}
