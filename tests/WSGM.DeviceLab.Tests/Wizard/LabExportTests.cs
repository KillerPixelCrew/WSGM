using System.IO.Compression;
using System.Text;
using WSGM.Device.Tests;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Prepare_RedactsEveryJsonStringAndExcludesOtherFiles()
    {
        using TemporaryDirectory temporary = new();
        var project = Project(temporary);
        var attempt = project.BeginAttempt(LabStages.Preflight, Now);
        project.WriteEvidence(attempt, "preflight", new
        {
            HidHide = new[] { @"C:\Users\Tester\Tools\app.exe" },
            Note = "plain"
        });
        File.WriteAllText(Path.Combine(attempt, "notes.txt"), @"C:\Users\Tester\secret");

        var export = LabExport.Prepare(project);

        var preflight = Assert.Single(export.Preview.Files,
            file => file.Path.EndsWith("preflight.json", StringComparison.Ordinal));
        Assert.True(preflight.Redacted);
        Assert.Contains(export.Preview.Excluded, line => line.Contains("notes.txt", StringComparison.Ordinal));
        Assert.Contains(export.Preview.Redactions, item => item.Category == RedactionCategory.ProfilePath);
        var text = Read(Written(temporary, export), preflight.Path);
        Assert.DoesNotContain("Tester", text, StringComparison.Ordinal);
        Assert.Contains("plain", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_UsesTheInventoryRedactionForInventories()
    {
        using TemporaryDirectory temporary = new();
        var project = Project(temporary);
        var attempt = project.BeginAttempt(LabStages.Identity, Now);
        var inventory = new MachineInventory
        {
            SchemaVersion = 1,
            Firmware = new FirmwareInventory { BaseboardProduct = "RC73XA" },
            UsbInterfaces =
            [
                new UsbInterfaceInventory
                {
                    InstanceId = @"USB\VID_0B05&PID_1B4C&MI_00\7&2A1B3C4D&0&0000",
                    LocationPath = "PCIROOT(0)#PCI(0801)#USBROOT(0)#USB(3)",
                    Present = true
                }
            ],
            CapturedAt = Now
        };
        DurableFile.WriteNewText(Path.Combine(attempt, "inventory.json"), DeviceLabJson.Serialize(inventory));

        var export = LabExport.Prepare(project);

        var file = Assert.Single(export.Preview.Files,
            file => file.Path.EndsWith("inventory.json", StringComparison.Ordinal));
        var text = Read(Written(temporary, export), file.Path);
        Assert.Contains("RC73XA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2A1B3C4D", text, StringComparison.Ordinal);
        Assert.DoesNotContain("USBROOT(0)#USB(3)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_CarriesAHashManifestAndRefusesAnExistingFile()
    {
        using TemporaryDirectory temporary = new();
        var export = LabExport.Prepare(Project(temporary));
        var target = Written(temporary, export);

        using (var archive = ZipFile.OpenRead(target))
        {
            Assert.NotNull(archive.GetEntry("report-manifest.json"));
            Assert.NotNull(archive.GetEntry(LabProject.ManifestFileName));
        }

        Assert.Throws<IOException>(() => export.Write(target, Boundaries(temporary)));
    }

    private static LabProject Project(TemporaryDirectory temporary)
    {
        return LabProject.Create(Path.Combine(temporary.Root, "project"), LabStages.Ids, "1.0.0", Now);
    }

    private static string Written(TemporaryDirectory temporary, LabExport export)
    {
        var target = Path.Combine(temporary.Root, "out", "report.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target))
        {
            export.Write(target, Boundaries(temporary));
        }

        return target;
    }

    private static string Read(string archivePath, string entryPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var reader = new StreamReader(archive.GetEntry(entryPath)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static DeviceLabPathBoundaries Boundaries(TemporaryDirectory temporary)
    {
        return new DeviceLabPathBoundaries { LiveDataDirectory = Path.Combine(temporary.Root, "live-wsgm") };
    }
}
