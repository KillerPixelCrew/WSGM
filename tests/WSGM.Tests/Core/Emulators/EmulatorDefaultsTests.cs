using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class EmulatorDefaultsTests
{
    [Fact]
    public void InstalledStandaloneWinsOverCoreAndRemovalFallsBackToKnownCore()
    {
        var standalone = new EmulatorInstallation
            { Id = "duckstation", Name = "DuckStation", Systems = ["psx"] };
        var retroarch = new EmulatorInstallation
        {
            Id = "retroarch", Name = "RetroArch", Systems = ["psx"],
            DataPolicy = new EmulatorDataPolicy { HasCores = true },
            Cores = [new EmulatorCore { Id = "beetle", Name = "Beetle", Systems = ["psx"] }]
        };
        var store = new EmulatorStore { Installations = [retroarch, standalone] };
        Assert.Equal("duckstation", ManagedContentStorage.PreferredSystem(store, "psx")!.InstallationId);
        var fallback = ManagedContentStorage.PreferredSystem(store with { Installations = [retroarch] }, "psx")!;
        Assert.Equal("retroarch", fallback.InstallationId);
        Assert.Equal("beetle", fallback.CoreId);
    }

    [Fact]
    public void ExplicitSelectionWinsAndUnknownCoreDoesNotBecomeAutomatic()
    {
        var preference = new EmulatorSystemPreference
            { SystemId = "psx", InstallationId = "my-choice", CoreId = "my-core" };
        Assert.Same(preference, ManagedContentStorage.PreferredSystem(
            new EmulatorStore { SystemPreferences = [preference] }, "psx"));
        var store = new EmulatorStore
        {
            Installations = [new EmulatorInstallation
            {
                Id = "retroarch", Systems = ["psx"], DataPolicy = new EmulatorDataPolicy { HasCores = true },
                Cores = [new EmulatorCore { Id = "unknown", MetadataMissing = true }]
            }]
        };
        Assert.Null(ManagedContentStorage.PreferredSystem(store, "psx"));
        Assert.Null(ManagedContentStorage.PreferredSystem(store, "ps2"));
    }
    [Theory]
    [InlineData("sega_saturn")]
    [InlineData("Sega - Saturn")]
    public void StoredCoreAliasesPopulateCanonicalSystemDefaults(string alias)
    {
        var store = new EmulatorStore
        {
            Installations = [new EmulatorInstallation
            {
                Id = "retroarch", Systems = [alias], DataPolicy = new EmulatorDataPolicy { HasCores = true },
                Cores = [new EmulatorCore { Id = "saturn", Systems = [alias] }]
            }]
        };
        Assert.Equal("saturn", ManagedContentStorage.PreferredSystem(store, "saturn")!.CoreId);
    }
}
