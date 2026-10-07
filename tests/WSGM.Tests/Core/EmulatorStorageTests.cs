using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class EmulatorStorageTests
{
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
