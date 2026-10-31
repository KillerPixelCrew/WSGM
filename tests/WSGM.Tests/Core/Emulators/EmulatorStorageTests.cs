using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class EmulatorStorageTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not a key file")]
    [InlineData("master_key_00 = invalid")]
    public void EmptyOrMalformedPrerequisitesDoNotMakeAnEmulatorReady(string keyText)
    {
        using TemporaryDirectory temporary = new();
        var keys = temporary.GetPath("keys");
        Directory.CreateDirectory(keys);
        File.WriteAllText(Path.Combine(keys, "prod.keys"), keyText);
        var installed = new EmulatorInstallation
        {
            DataPath = temporary.Root,
            DataPolicy = new EmulatorDataPolicy
            {
                Prerequisites = [new EmulatorPrerequisite
                {
                    Kind = "keys", Name = "Console keys", Required = true, Destination = "keys",
                    RequiredNames = ["prod.keys"]
                }]
            }
        };

        Assert.Single(EmulatorStorage.MissingPrerequisites(installed));
        File.WriteAllText(Path.Combine(keys, "prod.keys"), "master_key_00 = 0123456789abcdef0123456789abcdef");
        Assert.Empty(EmulatorStorage.MissingPrerequisites(installed));
    }

    [Fact]
    public void ValidateSelection_AcceptsSystemAliasesWithoutRequiringRuntimeFiles()
    {
        var core = new EmulatorCore { Id = "genesis_plus", Systems = ["megadrive"], Path = "absent.dll" };
        var installation = new EmulatorInstallation
        {
            DataPolicy = new EmulatorDataPolicy { HasCores = true },
            Cores = [core]
        };

        Assert.Same(core, EmulatorStorage.ValidateSelection(installation, "GENESIS", core.Id));
    }

    [Fact]
    public void ValidateSelection_RejectsKnownIncompatibleCore()
    {
        var installation = new EmulatorInstallation
        {
            DataPolicy = new EmulatorDataPolicy { HasCores = true },
            Cores = [new EmulatorCore { Id = "mgba", Systems = ["gba"] }]
        };

        Assert.Throws<InvalidOperationException>(() => EmulatorStorage.ValidateSelection(installation, "psx", "mgba"));
    }
}
