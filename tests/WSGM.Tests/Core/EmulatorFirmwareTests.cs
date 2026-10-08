using System.Security.Cryptography;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class EmulatorFirmwareTests
{
    [Fact]
    public void EmptyNativeFirmwareDirectoryDoesNotSatisfySetup()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Root, "registered"));
        var installed = new EmulatorInstallation
        {
            DataPath = temporary.Root,
            DataPolicy = new EmulatorDataPolicy
            {
                Prerequisites =
                [
                    new EmulatorPrerequisite
                    {
                        Name = "Console firmware", Required = true, NativeInstaller = true, Destination = "registered"
                    }
                ]
            }
        };
        Assert.Contains("Console firmware", Assert.Single(EmulatorStorage.MissingPrerequisites(installed)));
    }

    [Fact]
    public void CorruptContentIdLeavesTheActiveFirmwareUntouchedAndRemovesStaging()
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temporary.Root, "dump")).FullName;
        File.WriteAllBytes(Path.Combine(source, new string('0', 32) + ".nca"), new byte[4096]);
        var destination = Directory.CreateDirectory(Path.Combine(temporary.Root, "data", "registered")).FullName;
        File.WriteAllText(Path.Combine(destination, "existing.nca"), "original firmware");
        var installed = new EmulatorInstallation { DataPath = Path.Combine(temporary.Root, "data") };
        var rule = new EmulatorPrerequisite { Destination = "registered" };
        Assert.Throws<InvalidDataException>(() =>
            EmulatorFirmware.InstallEden(installed, rule, source, CancellationToken.None));
        Assert.Equal("original firmware", File.ReadAllText(Path.Combine(destination, "existing.nca")));
        Assert.Empty(Directory.EnumerateDirectories(installed.DataPath, ".wsgm-firmware-*"));
    }

    [Fact]
    public void ContentAddressedPackageActivatesTogetherAndRetainsPreviousFirmware()
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temporary.Root, "dump")).FullName;
        var bytes = new byte[4096];
        var name = Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant() + ".nca";
        File.WriteAllBytes(Path.Combine(source, name), bytes);
        var destination = Directory.CreateDirectory(Path.Combine(temporary.Root, "data", "registered")).FullName;
        File.WriteAllText(Path.Combine(destination, "existing.nca"), "original firmware");
        var installed = new EmulatorInstallation { DataPath = Path.Combine(temporary.Root, "data") };
        var rule = new EmulatorPrerequisite { Destination = "registered" };
        EmulatorFirmware.InstallEden(installed, rule, source, CancellationToken.None);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, name)));
        Assert.False(File.Exists(Path.Combine(destination, "existing.nca")));
        var backup = Assert.Single(Directory.EnumerateDirectories(installed.DataPath, "registered.before-wsgm-*"));
        Assert.Equal("original firmware", File.ReadAllText(Path.Combine(backup, "existing.nca")));
    }
}
