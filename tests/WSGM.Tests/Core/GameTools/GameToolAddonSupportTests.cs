using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class GameToolAddonSupportTests
{
    // An excerpt of OptiPatcher's dllmain.cpp, in the shape OptiScaler's setup script reads.
    private const string Source = """
                                  #define CHECK_UE(name) exeName == (#name "-win64-shipping.exe") || exeName == (#name "-wingdk-shipping.exe")

                                  static void CheckForPatch()
                                  {
                                      // Lost Records: Bloom & Rage, F1 Manager 2024
                                      if (exeName == "bloom&rage.exe" || exeName == "f1manager24.exe")
                                      {
                                      }
                                      // inline patch
                                      else if (CHECK_UE(mgsdelta) || exeName == 'arkascended.exe')
                                      {
                                      }
                                  }
                                  """;

    private static readonly GameToolAddon OptiPatcher =
        GameToolCatalog.Current.Find("optiscaler")!.Addon("optipatcher")!;

    [Fact]
    public void TheSupportListIsReadAsOptiScalersSetupScriptReadsIt()
    {
        var names = GameToolAddonSupport.Parse(Source, OptiPatcher.SupportRules);
        var support = new GameToolAddonSupport(names, null, DateTime.UtcNow);

        Assert.True(support.Supports("MGSDelta-Win64-Shipping.exe"));
        Assert.True(support.Supports("mgsdelta-wingdk-shipping.exe"));
        Assert.True(support.Supports("Bloom&Rage.exe"));
        Assert.True(support.Supports("f1manager24.exe"));
        Assert.True(support.Supports("ArkAscended.exe"));
        Assert.False(support.Supports("MGSDelta.exe"));
        Assert.False(support.Supports("DS2.exe"));
    }

    [Fact]
    public void AnUnreadListSupportsNothing()
    {
        var support = new GameToolAddonSupport(null, "Couldn't reach GitHub.", DateTime.UtcNow);

        Assert.False(support.Supports("MGSDelta-Win64-Shipping.exe"));
    }

    [Fact]
    public void TheCatalogPlacesOptiPatcherInThePluginsFolderBesideOptiScalerForEveryVersion()
    {
        // OptiScaler 0.9.x and 10.x both load *.asi from the relative "plugins" folder, beside the proxy DLL.
        Assert.Equal("plugins/OptiPatcher.asi", OptiPatcher.File);
        Assert.Equal(("Plugins", "LoadAsiPlugins", "true", "auto"),
            (OptiPatcher.ConfigSection, OptiPatcher.ConfigKey, OptiPatcher.ConfigOn, OptiPatcher.ConfigOff));
        Assert.Equal(["amd", "intel"], OptiPatcher.Vendors);
    }
}
