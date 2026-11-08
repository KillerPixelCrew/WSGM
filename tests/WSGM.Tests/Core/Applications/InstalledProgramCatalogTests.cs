using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class InstalledProgramCatalogTests
{
    private const string Xbox = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    private static InstalledApplication Desktop(string name, string target, string arguments = "")
    {
        return new InstalledApplication(name, target, null, target, arguments);
    }

    private static InstalledApplication Packaged(string name, string aumid)
    {
        return new InstalledApplication(name, aumid, aumid, null, "");
    }

    private static IReadOnlyList<InstalledProgram> Merge(params InstalledApplication[] listed)
    {
        return InstalledProgramCatalog.Merge(listed, _ => true);
    }

    [Fact]
    public void PackagedAndDesktopAppsMergeSortedByNameWithTheXboxAppLaunchedByItsId()
    {
        var programs = Merge(Desktop("Zeta", @"C:\Tools\zeta.exe"), Packaged("Xbox", Xbox),
            Desktop("Alpha", @"C:\Tools\alpha.exe"));

        Assert.Equal(["Alpha", "Xbox", "Zeta"], programs.Select(program => program.Name));
        var xbox = programs[1];
        Assert.Equal(Xbox, xbox.AppUserModelId);
        Assert.Null(xbox.Executable);
    }

    [Fact]
    public void TheSameAppInBothStartMenusIsListedOnce()
    {
        var programs = Merge(Desktop("Tool", @"C:\Tools\tool.exe"), Desktop("Tool (x64)", @"c:\tools\TOOL.exe"),
            Packaged("App", Xbox), Packaged("App again", Xbox.ToUpperInvariant()));

        Assert.Equal(2, programs.Count);
    }

    [Fact]
    public void TheSameExecutableWithOtherArgumentsIsADifferentApp()
    {
        var programs = Merge(Desktop("Browser", @"C:\Web\web.exe"),
            Desktop("GitHub", @"C:\Web\web.exe", "--app github"));

        Assert.Equal(2, programs.Count);
    }

    [Theory]
    [InlineData("Uninstall Tool", @"C:\Tools\tool.exe")]
    [InlineData("Tool deinstallieren", @"C:\Tools\tool.exe")]
    [InlineData("Tool Help", @"C:\Tools\tool.exe")]
    [InlineData("Tool", @"C:\Tools\unins000.exe")]
    [InlineData("Tool", @"C:\Tools\setup.exe")]
    [InlineData("Tool", @"C:\Tools\ToolHelper.exe")]
    [InlineData("Tool Docs", @"C:\Tools\manual.chm")]
    [InlineData("Site", @"C:\Tools\page.url")]
    [InlineData("File Explorer", @"C:\Windows\explorer.exe")]
    public void MaintenanceToolsAndNonExecutablesAreNotOffered(string name, string target)
    {
        Assert.Empty(Merge(Desktop(name, target)));
    }

    [Fact]
    public void ADesktopEntryWithoutAnExistingTargetIsNotOffered()
    {
        var listed = new[]
        {
            new InstalledApplication("No target", "id", null, null, ""),
            Desktop("Gone", @"C:\Tools\gone.exe")
        };

        Assert.Empty(InstalledProgramCatalog.Merge(listed, _ => false));
    }

    [Fact]
    public void APackagedAppIsKeptEvenWithAWordTheDesktopFilterWouldDrop()
    {
        // The package declared it for Start, so "Get Help" is a real app rather than a shortcut's readme.
        Assert.Single(Merge(Packaged("Get Help", "Microsoft.GetHelp_8wekyb3d8bbwe!App")));
    }
}
