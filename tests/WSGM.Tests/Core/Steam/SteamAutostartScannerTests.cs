using WSGM.Core;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class SteamAutostartScannerTests
{
    private const string SteamExe = @"C:\Program Files (x86)\Steam\steam.exe";

    [Theory]
    [InlineData(SteamAutostartKind.RunValue, "notsteam.exe", false)]
    [InlineData(SteamAutostartKind.RunValue, "theme-steam.exe", false)]
    [InlineData(SteamAutostartKind.RunValue, "STEAM.EXE", true)]
    [InlineData(SteamAutostartKind.StartupShortcut, "notsteam.exe", false)]
    [InlineData(SteamAutostartKind.StartupShortcut, "theme-steam.exe", false)]
    [InlineData(SteamAutostartKind.StartupShortcut, "steam.exe", true)]
    [InlineData(SteamAutostartKind.ScheduledTask, "notsteam.exe", false)]
    [InlineData(SteamAutostartKind.ScheduledTask, "theme-steam.exe", false)]
    [InlineData(SteamAutostartKind.ScheduledTask, "steam.exe", true)]
    public void UnknownInstallationMatchesOnlyTheExactExecutableFilename(SteamAutostartKind kind,
        string executable, bool expected)
    {
        FakeAutostartSystem system = new();
        var path = @"C:\Tools\" + executable;
        switch (kind)
        {
            case SteamAutostartKind.RunValue:
                system.Run[(SteamAutostartScope.User, false)] = new Dictionary<string, string>
                    { ["Tool"] = path + " -start" };
                break;
            case SteamAutostartKind.StartupShortcut:
                system.Shortcuts[SteamAutostartScope.User] = new Dictionary<string, string> { ["Tool.lnk"] = path };
                break;
            case SteamAutostartKind.ScheduledTask:
                system.Tasks[@"\Tool"] = path + " -start";
                system.TaskEnabled[@"\Tool"] = true;
                break;
        }

        var found = SteamAutostartScanner.Scan(system, null);
        Assert.Equal(expected ? 1 : 0, found.Count);
        Assert.Empty(system.Writes);
    }

    private static FakeAutostartSystem WithRunValue(string name, string command)
    {
        return new FakeAutostartSystem
            { Run = { [(SteamAutostartScope.User, false)] = new Dictionary<string, string> { [name] = command } } };
    }

    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe -silent")]
    [InlineData(@"c:\program files (x86)\steam\STEAM.EXE")]
    public void EveryWayOfSpellingSteamsOwnEntryIsFound(string command)
    {
        var found = SteamAutostartScanner.Scan(WithRunValue("Steam", command), SteamExe);

        var source = Assert.Single(found);
        Assert.Equal(SteamAutostartKind.RunValue, source.Kind);
        Assert.True(source.Enabled);
        Assert.False(source.NeedsElevation);
    }

    [Theory]
    // Another program that merely mentions Steam, and Steam's own tools, are not Steam.
    [InlineData(@"C:\Program Files\Wallpaper Engine\wallpaper64.exe -steam")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamerrorreporter.exe")]
    [InlineData("")]
    public void SomethingElseIsLeftAlone(string command)
    {
        Assert.Empty(SteamAutostartScanner.Scan(WithRunValue("Other", command), SteamExe));
    }

    [Fact]
    public void ASecondSteamInstallationIsNotThisOne()
    {
        Assert.Empty(SteamAutostartScanner.Scan(
            WithRunValue("Steam", @"D:\Games\Steam\steam.exe"), SteamExe));
    }

    [Fact]
    public void WithoutAKnownInstallationAnySteamCounts()
    {
        var found = SteamAutostartScanner.Scan(WithRunValue("Steam", @"D:\Games\Steam\steam.exe"), null);

        Assert.Single(found);
    }

    [Fact]
    public void AnEnvironmentVariableInTheCommandIsExpanded()
    {
        var system = WithRunValue("Steam", "\"%ProgramFiles(x86)%\\Steam\\steam.exe\" -silent");

        Assert.Single(SteamAutostartScanner.Scan(system, SteamExe));
    }

    [Theory]
    // Byte zero carries the flag; an absent value is Windows' enabled default.
    [InlineData(null, true)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void WindowsOwnApprovalStateIsRead(byte[]? approval, bool expected)
    {
        var system = WithRunValue("Steam", SteamExe);
        if (approval is not null)
        {
            system.Approvals[(SteamAutostartScope.User, "Run", "Steam")] = approval;
        }

        Assert.Equal(expected, Assert.Single(SteamAutostartScanner.Scan(system, SteamExe)).Enabled);
    }

    [Fact]
    public void AStartupShortcutAndALogonTaskAreBothFound()
    {
        FakeAutostartSystem system = new()
        {
            Shortcuts = { [SteamAutostartScope.User] = new Dictionary<string, string> { ["Steam.lnk"] = SteamExe } },
            Tasks = { [@"\Steam"] = SteamExe },
            TaskEnabled = { [@"\Steam"] = true }
        };

        var found = SteamAutostartScanner.Scan(system, SteamExe);

        Assert.Equal(2, found.Count);
        var shortcut = found.Single(source => source.Kind is SteamAutostartKind.StartupShortcut);
        Assert.False(shortcut.NeedsElevation);
        var task = found.Single(source => source.Kind is SteamAutostartKind.ScheduledTask);
        // A task always needs elevation to change, whoever registered it.
        Assert.True(task.NeedsElevation);
        Assert.Equal(@"\Steam", task.Location);
    }

    [Fact]
    public void AMachineRunValueNeedsElevation()
    {
        FakeAutostartSystem system = new()
        {
            Run = { [(SteamAutostartScope.Machine, true)] = new Dictionary<string, string> { ["Steam"] = SteamExe } }
        };

        var source = Assert.Single(SteamAutostartScanner.Scan(system, SteamExe));
        Assert.True(source.NeedsElevation);
        Assert.True(source.Wow64);
    }

    [Fact]
    public void TaskDefinitionsAreSplitAndNamedByTheirOwnUri()
    {
        const string output = """
                              <?xml version="1.0" encoding="UTF-16"?>
                              <!-- \Other -->
                              <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                                <RegistrationInfo><URI>\Other</URI></RegistrationInfo>
                                <Actions><Exec><Command>C:\other.exe</Command></Exec></Actions>
                              </Task>
                              <?xml version="1.0" encoding="UTF-16"?>
                              <!-- \Steam -->
                              <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                                <RegistrationInfo><URI>\Steam</URI></RegistrationInfo>
                                <Triggers><LogonTrigger /></Triggers>
                                <Settings><Enabled>false</Enabled></Settings>
                                <Actions><Exec><Command>C:\steam.exe</Command></Exec></Actions>
                              </Task>
                              """;

        var definitions = AutostartSystem.SplitTaskDefinitions(output).ToArray();

        Assert.Equal([@"\Other", @"\Steam"], definitions.Select(entry => entry.Path));
        var ns = definitions[1].Definition.Name.Namespace;
        Assert.Equal("false", definitions[1].Definition.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value);
    }
}
