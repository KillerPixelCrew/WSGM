using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     Reading a GDK title's game configuration. The schema varies across GDK versions, so anything
///     this parser does not model is reported rather than dropped: that is how the real shape gets
///     learned from installed titles before a rule depends on more of it.
/// </summary>
public sealed class MicrosoftGameConfigTests
{
    private const string Config = """
                                  <?xml version="1.0" encoding="utf-8"?>
                                  <Game configVersion="1">
                                    <Identity Name="Publisher.Washer" Publisher="CN=Publisher" Version="1.0.286.0" />
                                    <ExecutableList>
                                      <Executable Name="Washer.exe" Id="Game" />
                                    </ExecutableList>
                                    <ShellVisuals DefaultDisplayName="Washer" PublisherDisplayName="Publisher" />
                                    <StoreId>9NBLGGH1234</StoreId>
                                    <TitleId>1A2B3C4D</TitleId>
                                    <DesktopRegistration><Something /></DesktopRegistration>
                                  </Game>
                                  """;

    [Fact]
    public void AConfigReadsItsExecutableNameAndIds()
    {
        var facts = MicrosoftGameConfig.Parse(Config);

        Assert.True(facts.Readable);
        Assert.Equal("Washer.exe", Assert.Single(facts.Executables));
        Assert.Equal("Washer", facts.DisplayName);
        Assert.Equal("9NBLGGH1234", facts.StoreId);
        Assert.Equal("1A2B3C4D", facts.TitleId);
    }

    [Fact]
    public void AnElementThisParserDoesNotModelIsReported()
    {
        Assert.Contains("DesktopRegistration", MicrosoftGameConfig.Parse(Config).UnrecognisedElements);
    }

    [Fact]
    public void AModelledElementIsNotReportedAsUnrecognised()
    {
        var facts = MicrosoftGameConfig.Parse(Config);

        Assert.DoesNotContain("ExecutableList", facts.UnrecognisedElements);
        Assert.DoesNotContain("ShellVisuals", facts.UnrecognisedElements);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("")]
    [InlineData(null)]
    public void AConfigThatCannotBeParsedReportsItself(string? xml)
    {
        Assert.False(MicrosoftGameConfig.Parse(xml).Readable);
    }

    [Fact]
    public void AnOversizeConfigIsRefusedRatherThanParsed()
    {
        Assert.False(MicrosoftGameConfig.Parse(new string('x', MicrosoftGameConfig.MaximumBytes + 1))
            .Readable);
    }

    [Fact]
    public void AnExternalEntityIsNotExpanded()
    {
        var config = """
                     <?xml version="1.0"?>
                     <!DOCTYPE Game [<!ENTITY secret SYSTEM "file:///C:/Windows/win.ini">]>
                     <Game><StoreId>&secret;</StoreId></Game>
                     """;

        Assert.False(MicrosoftGameConfig.Parse(config).Readable);
    }
}
