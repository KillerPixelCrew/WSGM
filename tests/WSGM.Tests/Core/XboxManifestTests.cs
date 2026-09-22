using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     Reading a package's own manifest. Namespaces are versioned, so elements are matched by local
///     name; matching the namespace would silently stop recognising titles built against a newer SDK.
/// </summary>
public sealed class XboxManifestTests
{
    private const string Uwp = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="Publisher.Game" Publisher="CN=Publisher" Version="1.0.0.0" />
          <Properties><DisplayName>Moonlit</DisplayName></Properties>
          <Applications>
            <Application Id="App" Executable="game.exe" EntryPoint="Game.App" />
          </Applications>
          <Capabilities><Capability Name="internetClient" /></Capabilities>
        </Package>
        """;

    private const string FullTrust = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
          <Identity Name="Publisher.Washer" Publisher="CN=Publisher" Version="1.0.286.0" />
          <Properties><DisplayName>Washer</DisplayName></Properties>
          <Dependencies>
            <PackageDependency Name="Microsoft.GamingServices" MinVersion="1.0.0.0" />
          </Dependencies>
          <Applications>
            <Application Id="Game" Executable="gamelaunchhelper.exe"
                         EntryPoint="Windows.FullTrustApplication" />
          </Applications>
          <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
        </Package>
        """;

    [Fact]
    public void AUwpManifestReadsAsItsApplicationAndCapabilities()
    {
        var facts = XboxManifest.ParseAppxManifest(Uwp);

        Assert.True(facts.ManifestReadable);
        Assert.Equal("App", facts.ApplicationId);
        Assert.Equal("Game.App", facts.EntryPoint);
        Assert.Equal("game.exe", facts.Executable);
        Assert.False(facts.HasRunFullTrust);
        Assert.Equal(1, facts.ApplicationCount);
    }

    [Fact]
    public void AFullTrustManifestReadsItsCapabilityAndDependency()
    {
        var facts = XboxManifest.ParseAppxManifest(FullTrust);

        Assert.True(facts.HasRunFullTrust);
        Assert.Equal("Windows.FullTrustApplication", facts.EntryPoint);
        Assert.Equal("gamelaunchhelper.exe", facts.Executable);
        Assert.Contains("Microsoft.GamingServices", facts.PackageDependencies);
    }

    [Fact]
    public void AnExecutableWithAPathReadsAsItsFileName()
    {
        var manifest = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="App" Executable="bin/win64/game.exe" EntryPoint="Game.App" />
              </Applications>
            </Package>
            """;

        Assert.Equal("game.exe", XboxManifest.ParseAppxManifest(manifest).Executable);
    }

    [Fact]
    public void AskingForAnApplicationThePackageDoesNotDeclareDescribesNothing()
    {
        // Describing a different application would classify the wrong thing.
        var facts = XboxManifest.ParseAppxManifest(Uwp, "NotHere");

        Assert.Equal(string.Empty, facts.ApplicationId);
        Assert.Equal(1, facts.ApplicationCount);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<Package><unclosed></Package>")]
    [InlineData("")]
    [InlineData(null)]
    public void AManifestThatCannotBeParsedReportsItself(string? xml)
    {
        Assert.False(XboxManifest.ParseAppxManifest(xml).ManifestReadable);
    }

    [Fact]
    public void AnExternalEntityIsNotExpanded()
    {
        // A manifest is a file out of somebody's package. An entity in one must not become a read
        // of something else on this machine.
        var manifest = """
            <?xml version="1.0"?>
            <!DOCTYPE Package [<!ENTITY secret SYSTEM "file:///C:/Windows/win.ini">]>
            <Package><Applications><Application Id="&secret;" /></Applications></Package>
            """;

        Assert.False(XboxManifest.ParseAppxManifest(manifest).ManifestReadable);
    }

    [Fact]
    public void AnOversizeManifestIsRefusedRatherThanParsed()
    {
        Assert.False(XboxManifest.ParseAppxManifest(new string('x', XboxManifest.MaximumBytes + 1))
            .ManifestReadable);
    }

    [Fact]
    public void ADisplayNameIsReadFromTheManifest()
    {
        Assert.Equal("Moonlit", XboxManifest.ParseDisplayName(Uwp));
    }

    [Fact]
    public void AnIndirectDisplayNameIsReportedAsAbsent()
    {
        // Showing it literally would put "ms-resource:AppName" in somebody's Steam library.
        var manifest = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Properties><DisplayName>ms-resource:AppName</DisplayName></Properties>
            </Package>
            """;

        Assert.Equal(string.Empty, XboxManifest.ParseDisplayName(manifest));
    }
}
