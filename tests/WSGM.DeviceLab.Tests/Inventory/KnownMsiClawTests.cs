using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Probes;

namespace WSGM.Device.Tests;

public sealed class KnownMsiClawTests
{
    [Fact]
    public void KnownMsiClaw_ExactFingerprintOwnsExactlyFiveCompiledReadProbes()
    {
        var fingerprint = KnownMsiClaw.Create();
        var exact = KnownDeviceMatcher.Assess(
            Inventory(fingerprint),
            fingerprint,
            fingerprint.DeviceId);

        Assert.True(exact.ExactMatch, string.Join(Environment.NewLine, exact.Explanations));
        Assert.Equal(5, fingerprint.ReadProbes.Count);
        Assert.Equal(5, fingerprint.ReadProbes.Select(probe => probe.Id).Distinct().Count());
        Assert.All(fingerprint.ReadProbes, probe =>
        {
            Assert.Empty(ReadProbeMetadataPolicy.Validate(probe));
            Assert.True(BuiltInReadProbeRegistry.TryResolve(probe.Id, probe.Version, out _));
        });

        var mismatch = Inventory(fingerprint) with
        {
            Firmware = Inventory(fingerprint).Firmware with { BaseboardProduct = "NOT-MS-1T52" }
        };
        Assert.False(KnownDeviceMatcher.Assess(
            mismatch,
            fingerprint,
            fingerprint.DeviceId).ExactMatch);
    }

    [Fact]
    public void KnownMsiClaw_UnavailableWmiNamespaceDoesNotSatisfyTheExactProviderGate()
    {
        var fingerprint = KnownMsiClaw.Create();
        var inventory = Inventory(fingerprint);
        var unavailable = inventory with
        {
            WmiClasses =
            [
                inventory.WmiClasses[0] with { Access = WmiAccess.NamespaceUnavailable }
            ]
        };
        var accessDenied = inventory with
        {
            WmiClasses =
            [
                inventory.WmiClasses[0] with { Access = WmiAccess.AccessDenied }
            ]
        };

        Assert.False(KnownDeviceMatcher.Assess(
            unavailable,
            fingerprint,
            fingerprint.DeviceId).ExactMatch);
        Assert.True(KnownDeviceMatcher.Assess(
            accessDenied,
            fingerprint,
            fingerprint.DeviceId).ExactMatch);
    }

    [Fact]
    public void PluginIdentity_UnavailableWmiNamespaceDoesNotClaimAProviderSignature()
    {
        var fingerprint = KnownMsiClaw.Create();
        var inventory = Inventory(fingerprint);
        var unavailable = inventory with
        {
            WmiClasses =
            [
                inventory.WmiClasses[0] with { Access = WmiAccess.NamespaceUnavailable }
            ]
        };
        var accessDenied = inventory with
        {
            WmiClasses =
            [
                inventory.WmiClasses[0] with { Access = WmiAccess.AccessDenied }
            ]
        };

        string[] expected = [$"{fingerprint.WmiNamespace}:{fingerprint.WmiClass}"];
        Assert.Empty(DeviceLabApplication.ToPluginIdentity(unavailable).WmiProviderSignatures);
        Assert.Equal(expected, DeviceLabApplication.ToPluginIdentity(accessDenied).WmiProviderSignatures);
    }

    private static MachineInventory Inventory(KnownDeviceFingerprint fingerprint) => new()
    {
        SchemaVersion = 1,
        Firmware = new FirmwareInventory
        {
            SystemManufacturer = fingerprint.SystemManufacturer,
            BaseboardProduct = fingerprint.BaseboardProduct,
            SystemSku = fingerprint.SystemSku
        },
        UsbInterfaces =
        [
            new UsbInterfaceInventory
            {
                InstanceId = "synthetic-instance",
                VendorId = fingerprint.UsbVendorId,
                ProductId = fingerprint.UsbProductIds[0],
                DeviceRelease = fingerprint.UsbDeviceRelease,
                Present = true
            }
        ],
        WmiClasses =
        [
            new WmiClassInventory
            {
                Namespace = fingerprint.WmiNamespace,
                ClassName = fingerprint.WmiClass,
                Access = WmiAccess.Available
            }
        ],
        CapturedAt = DateTimeOffset.UnixEpoch
    };
}
