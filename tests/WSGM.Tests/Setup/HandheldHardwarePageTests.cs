using WSGM.Device.Sdk.Identity;
using WSGM.Install;
using WSGM.Setup.UI;

namespace WSGM.Tests.Setup;

public sealed class HandheldHardwarePageTests
{
    [Fact]
    public void DecliningNativeSupportLeavesNoSelectedDeviceOrControllerComponents()
    {
        var offers = PluginOffers.Compute(new BundleManifest { SchemaVersion = 1, WsgmVersion = "2.1.0" },
            new DeviceIdentitySnapshot
            {
                BaseboardManufacturer = "ASUSTeK COMPUTER INC.", BaseboardProduct = "RC72LA"
            }, [], []);
        var page = new HardwarePage("Ally X", "RC72LA", offers, () => { });

        Assert.NotNull(page.Chosen);
        Assert.False(page.NeedsChoice);
        Assert.Contains(SetupComponent.ControllerStack, page.Chosen.Offer.Components);
        page.SkipPlugin = true;
        Assert.Null(page.Chosen);
        Assert.Empty(page.WillInstall);
        page.InstallPlugin = true;
        Assert.Equal(offers.Handheld, page.Chosen?.Offer);
    }
}
