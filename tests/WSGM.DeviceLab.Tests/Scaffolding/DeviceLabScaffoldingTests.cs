using System.Security.Cryptography;
using System.Text.Json;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Scaffolding;
using WSGM.Testing;

namespace WSGM.DeviceLab.Tests.Scaffolding;

public sealed class DeviceLabScaffoldingTests
{
    [Fact]
    public async Task Scaffold_OutsideCheckout_ProducesLibrarySourceWithoutPluginDependencies()
    {
        using TemporaryDirectory temporary = new();
        var capturePath = temporary.GetPath("source.wsgmcap");
        await using (FileStream capture = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CaptureBundleWriter.Write(capture, Capture());
        }

        var result = ScaffoldFromCaptureWorkflow.Run(capturePath, temporary.GetPath("contribution"),
            new DeviceLabPathBoundaries
            {
                LiveDataDirectory = temporary.GetPath("never-live-wsgm"), BroadHomeDirectories = []
            });
        Assert.StartsWith("LibHandheld.Families.", result.RootNamespace, StringComparison.Ordinal);
        Assert.Contains("DeviceIdentity.cs", result.Files);
        Assert.Contains("ReportDecoder.cs", result.Files);
        Assert.Contains("contribution.json", result.Files);
        Assert.Contains(result.Files, path => path.StartsWith("fixtures", StringComparison.Ordinal));
        Assert.DoesNotContain("plugin.wsgm.json", result.Files);
        Assert.Empty(Directory.EnumerateFiles(result.OutputDirectory, "*.csproj"));
        var identity = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "DeviceIdentity.cs"));
        Assert.Contains("using LibHandheld.Contracts;", identity, StringComparison.Ordinal);
        Assert.Contains("BOARD-X1", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("WSGM.Device.Sdk", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", identity, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "contribution.json")));
        Assert.False(metadata.RootElement.GetProperty("implemented").GetBoolean());
        Assert.Empty(metadata.RootElement.GetProperty("observedCapabilities").EnumerateArray());
        using var fixture = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "fixtures", "fixture.json")));
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(capturePath)));
        Assert.Equal(sourceHash, fixture.RootElement.GetProperty("sourceCaptureSha256").GetString());
        Assert.Equal("SimulatorOnly", fixture.RootElement.GetProperty("replayPolicy").GetString());
        var decoder = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "ReportDecoder.cs"));
        Assert.Contains("return false;", decoder, StringComparison.Ordinal);
        Assert.Contains("not a working driver", decoder, StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_PreCancelledRequestPublishesNoPartialDirectory()
    {
        using TemporaryDirectory temporary = new();
        var capturePath = temporary.GetPath("source.wsgmcap");
        using (FileStream capture = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CaptureBundleWriter.Write(capture, Capture());
        }

        var output = temporary.GetPath("cancelled-scaffold");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = Assert.Throws<OperationCanceledException>(() => ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            output,
            new DeviceLabPathBoundaries
            {
                LiveDataDirectory = temporary.GetPath("never-live-wsgm"),
                BroadHomeDirectories = []
            },
            cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(temporary.Root, ".cancelled-scaffold.*.tmp"));
    }

    [Fact]
    public void Scaffold_MultipleExactUsbEndpointsRequireAnExplicitInstance()
    {
        using TemporaryDirectory temporary = new();
        var original = Capture();
        var multiple = original with
        {
            Inventory = original.Inventory with
            {
                UsbInterfaces =
                [
                    original.Inventory.UsbInterfaces[0] with { InstanceId = "usb-left" },
                    original.Inventory.UsbInterfaces[0] with { InstanceId = "usb-right" }
                ]
            }
        };
        var capturePath = temporary.GetPath("multiple.wsgmcap");
        using (FileStream capture = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CaptureBundleWriter.Write(capture, multiple);
        }

        DeviceLabPathBoundaries boundaries = new()
        {
            LiveDataDirectory = temporary.GetPath("never-live-wsgm"),
            BroadHomeDirectories = []
        };

        var ambiguous = Assert.Throws<InvalidDataException>(() =>
            ScaffoldFromCaptureWorkflow.Run(
                capturePath,
                temporary.GetPath("ambiguous"),
                boundaries));
        var selected = ScaffoldFromCaptureWorkflow.Run(
            capturePath,
            temporary.GetPath("selected"),
            boundaries,
            "usb-right");

        Assert.Contains("Select one exact instance ID", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal("CAFE", selected.Identity.UsbVendorId);
        Assert.True(Directory.Exists(selected.OutputDirectory));
    }

    private static SanitizedCaptureBundle Capture()
    {
        var timestamp = DateTimeOffset.UnixEpoch;
        return new SanitizedCaptureBundle
        {
            Manifest = new ShareableCaptureManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                BundleId = "scaffold-test",
                ToolVersion = "test-1",
                StartedAt = timestamp,
                CompletedAt = timestamp,
                QpcFrequency = 1
            },
            Recipe = new ObserveOnlyRecipe
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                RecipeId = "scaffold-test",
                DisplayName = "Scaffold test"
            },
            Inventory = new MachineInventory
            {
                SchemaVersion = 1,
                Firmware = new FirmwareInventory
                {
                    SystemManufacturer = "Contoso Devices",
                    BaseboardProduct = "BOARD-X1",
                    SystemSku = "BOARD-X1-SKU",
                    BiosVersion = "1.0.0"
                },
                UsbInterfaces =
                [
                    new UsbInterfaceInventory
                    {
                        InstanceId = "redacted-instance",
                        VendorId = "CAFE",
                        ProductId = "BEEF",
                        DeviceRelease = "0100",
                        Present = true
                    }
                ],
                CapturedAt = timestamp
            },
            Redaction = new CaptureRedactionManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                DefaultRedactionApplied = true
            }
        };
    }
}
