using System.Text.Json.Nodes;
using WSGM.Device.Sdk.Identity;
using WSGM.Install;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class HandheldInstallChoicesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpdatePreservesNativeIntegrationChoiceWithoutInstalledFamilyPackage(bool enabled)
    {
        using var installation = new SetupTestInstallation();
        installation.Engine.ReadHardwareOffers(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = "ASUSTeK COMPUTER INC.", BaseboardProduct = "RC72LA"
        }, []);
        var answers = new JsonObject { ["deviceIntegration"] = enabled };

        var choices = installation.Engine.KeptChoices(answers, []);

        Assert.Equal(enabled, answers["deviceIntegration"]!.GetValue<bool>());
        Assert.Equal(enabled ? installation.Engine.Offers!.Handheld!.Definition.Id : null,
            choices.HandheldDefinitionId);
        IReadOnlyList<SetupComponent> expected = enabled ? [SetupComponent.ControllerStack] : [];
        Assert.Equal(expected, installation.Engine.RequiredComponents(choices));
        Assert.DoesNotContain(installation.Engine.PlanInstall(choices),
            step => step.Label.StartsWith("Installing ROG", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclinedOrWrongModelDoesNotRequestControllerDrivers()
    {
        using var installation = new SetupTestInstallation();
        installation.Engine.ReadHardwareOffers(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = "ASUSTeK COMPUTER INC.", BaseboardProduct = "RC72LA"
        }, []);
        Assert.Empty(installation.Engine.RequiredComponents(new InstallChoices(null, [], new JsonObject())));
        Assert.Empty(installation.Engine.RequiredComponents(new InstallChoices("unknown", [], new JsonObject())));
    }
}
