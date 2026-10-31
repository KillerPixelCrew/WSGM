using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class Pcsx2DataTests
{
    [Theory]
    [InlineData("stable", "2.6.3")]
    [InlineData("stable", "2.8.2")]
    [InlineData("nightly", "2.9.1")]
    public void PortableBindingKeepsTheExistingDataAndNormalizesOnlyOwnedLaunchFields(string channel, string version)
    {
        using var temporary = new TemporaryDirectory();
        var root = Directory.CreateDirectory(temporary.GetPath("PCSX2 program")).FullName;
        var program = Directory.CreateDirectory(Path.Combine(root, "versions", "current")).FullName;
        var data = Directory.CreateDirectory(temporary.GetPath("Retained user data")).FullName;
        var cards = Directory.CreateDirectory(Path.Combine(data, "memcards")).FullName;
        var save = Path.Combine(cards, "Mcd001.ps2");
        File.WriteAllText(save, "retained memory card");
        var installed = new EmulatorInstallation
        {
            Id = "existing-id", DefinitionId = "pcsx2", Name = "PCSX2", Managed = true,
            Root = root, DataPath = data, Version = version, Channel = channel,
            ExecutablePath = Path.Combine(program, "pcsx2-qt.exe"),
            LaunchArguments = ["-datapath", "{data}", "-batch", "--", "{rom}"],
            DataPolicy = new EmulatorDataPolicy { DataArguments = ["-datapath", "{data}"] }
        };
        var normalized = Pcsx2Data.Normalize(installed);
        Pcsx2Data.Prepare(normalized, staged: true);
        var relative = File.ReadAllText(Path.Combine(program, "portable.txt"));
        Assert.False(Path.IsPathRooted(relative));
        Assert.Equal(data, Path.GetFullPath(Path.Combine(program, relative)));
        Assert.Equal(installed.Id, normalized.Id);
        Assert.Equal(installed.DataPath, normalized.DataPath);
        Assert.Equal(new[] { "-batch", "--", "{rom}" }, normalized.LaunchArguments);
        Assert.Empty(normalized.DataPolicy.DataArguments);
        Assert.Equal("retained memory card", File.ReadAllText(save));
        Assert.Same(normalized, Pcsx2Data.Normalize(normalized));
    }

    [Fact]
    public void ExternalPcsx2IsLeftUntouched()
    {
        var installed = new EmulatorInstallation { DefinitionId = "pcsx2", Managed = false };
        Assert.Same(installed, Pcsx2Data.Normalize(installed));
        Pcsx2Data.Prepare(installed);
    }

    [Fact]
    public void VersionQueryExitOneIsAcceptedButAnUnknownOptionIsRefused()
    {
        var definition = new EmulatorPackageDefinition
        {
            Id = "pcsx2", ValidationExitCodes = [0, 1], ValidationOutput = "PCSX2"
        };
        Assert.True(EmulatorValidation.Accepted(definition, 1, "PCSX2 v2.8.2\nhttps://pcsx2.net/\n"));
        Assert.False(EmulatorValidation.Accepted(definition, 1,
            "PCSX2 v2.6.3\nUnknown parameter: '-datapath'\n"));
        Assert.False(EmulatorValidation.Accepted(definition, 1, "missing runtime dependency"));
    }
}
