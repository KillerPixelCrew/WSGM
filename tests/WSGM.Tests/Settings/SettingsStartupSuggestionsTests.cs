using WSGM.Core;
using WSGM.Settings;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

public sealed class SettingsStartupSuggestionsTests
{
    [Fact]
    public void AddingASuggestionCarriesItsLauncherArguments()
    {
        var config = new AppConfig();
        var services = SettingsTestServices.Inert(config) with
        {
            DetectStartupApps = () =>
            [
                ("AMD Software: Adrenalin Edition (background host)", @"C:\AMD\cncmd.exe", "restart", false),
                ("RTSS", @"C:\RTSS\RTSS.exe", "", true)
            ]
        };
        var model = new SettingsViewModel(config, services);

        Assert.Equal(["AMD Software: Adrenalin Edition (background host)", "RTSS", "Choose a program…"],
            model.StartupSuggestions);
        Assert.True(model.AddSelectedStartupApp());
        model.SelectedSuggestionIndex = 1;
        Assert.True(model.AddSelectedStartupApp());

        Assert.Collection(model.StartupApps,
            row =>
            {
                Assert.Equal(@"C:\AMD\cncmd.exe", row.Path);
                Assert.Equal("restart", row.Args);
                Assert.False(row.Elevated);
                Assert.True(row.Enabled);
            },
            row =>
            {
                Assert.Equal(@"C:\RTSS\RTSS.exe", row.Path);
                Assert.Equal("", row.Args);
                Assert.True(row.Elevated);
            });
    }

    [Fact]
    public void ThePlaceholderEntryLeavesTheChoiceToTheFilePicker()
    {
        var config = new AppConfig();
        var model = new SettingsViewModel(config, SettingsTestServices.Inert(config));

        Assert.Equal(["Choose a program…"], model.StartupSuggestions);
        Assert.False(model.AddSelectedStartupApp());
        Assert.Empty(model.StartupApps);
    }
}
