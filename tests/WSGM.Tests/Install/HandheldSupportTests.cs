using LibHandheld.Contracts;
using WSGM.Install;

namespace WSGM.Tests.Install;

public sealed class HandheldSupportTests
{
    [Theory]
    [InlineData("Micro-Star International Co., Ltd.", "MS-1T52", "msi-claw")]
    [InlineData("ASUSTeK COMPUTER INC.", "RC72LA", "rog-ally")]
    public void ExactNativeSupportNeedsNoInstalledOrBundledDevicePackage(string manufacturer, string board,
        string family)
    {
        var identity = new DeviceIdentitySnapshot { BaseboardManufacturer = manufacturer, BaseboardProduct = board };
        var offers = PluginOffers.Compute(new BundleManifest { SchemaVersion = 1, WsgmVersion = "2.1.0" }, identity, [],
            []);

        Assert.NotNull(offers.Handheld);
        Assert.Equal(family, offers.Handheld.Definition.FamilyId);
        Assert.Equal([SetupComponent.ControllerStack], offers.Handheld.Components);
    }

    [Theory]
    [InlineData("Unknown", "MS-1T52")]
    [InlineData("ASUSTeK COMPUTER INC.", "RC72LA-future")]
    [InlineData("Micro-Star International Co., Ltd.", "unknown")]
    public void FamilyResemblanceDoesNotOfferAnUnimplementedBackend(string manufacturer, string board)
    {
        Assert.Null(HandheldSupport.Detect(new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = manufacturer, BaseboardProduct = board
        }));
    }
}
