using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Tests;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Scaffolding;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabReviewTests
{
    private const string ClawRecord = "wsgm.claw-8-a2vm";
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Review_ListsConfirmationsAndDisagreementsPerField()
    {
        using TemporaryDirectory temporary = new();
        var report = Report(temporary);

        var review = LabReview.Review(report);

        Assert.Equal(ClawRecord, review.RecordId);
        Assert.Null(review.RecordProblem);
        Assert.Equal(LabReviewVerdict.Confirmed, Item(review, "identity").Verdict);
        Assert.Equal(LabReviewVerdict.Confirmed, Item(review, "button:oem-left").Verdict);
        Assert.Equal(LabReviewVerdict.Confirmed, Item(review, "button:back-left1").Verdict);
        var oemRight = Item(review, "button:oem-right");
        Assert.Equal(LabReviewVerdict.Disagrees, oemRight.Verdict);
        Assert.Contains("keys F15", oemRight.Observed, StringComparison.Ordinal);
        Assert.False(oemRight.PromotedByDefault);
        Assert.True(oemRight.Promotable);
        Assert.Equal(LabReviewVerdict.Observed, Item(review, "button:a").Verdict);
        Assert.Equal(LabReviewVerdict.Confirmed, Item(review, "motion:gyrometer").Verdict);
        Assert.Equal(LabReviewVerdict.Disagrees, Item(review, "motion:accelerometer").Verdict);
        Assert.Equal(LabReviewVerdict.Confirmed, Item(review, "rumble:hid-output").Verdict);
        Assert.Equal(LabReviewVerdict.Confirmed, Item(review, "mechanism:tdp:wmi-method").Verdict);
        Assert.Equal(LabReviewVerdict.Disagrees, Item(review, "mechanism:charge-limit:wmi-method").Verdict);
        Assert.Equal(LabReviewVerdict.Unresolved, Item(review, "mechanism:fan:wmi-method").Verdict);
        Assert.Equal(LabReviewVerdict.Unresolved, Item(review, "sleep").Verdict);
    }

    [Fact]
    public void Review_WithoutARecord_ProposesTheObservedIdentity()
    {
        using TemporaryDirectory temporary = new();
        var report = Report(temporary, false);

        var review = LabReview.Review(report);

        Assert.Null(review.RecordId);
        var identity = Item(review, "identity");
        Assert.Equal(LabReviewVerdict.New, identity.Verdict);
        Assert.Equal(LabReviewVerdict.New, Item(review, "button:oem-left").Verdict);
    }

    [Fact]
    public void Promote_WritesAParsableRecordWithLabConfirmedProvenance()
    {
        using TemporaryDirectory temporary = new();
        var report = Report(temporary);
        var output = Path.Combine(temporary.Root, "out", "record.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var result = LabPromote.Run(report, output, [], Boundaries(temporary));

        var record = DeviceKnowledgeBase.Parse(File.ReadAllText(output));
        Assert.Equal(ClawRecord, record.Id);
        Assert.Contains("button:oem-left", result.Promoted);
        Assert.DoesNotContain("button:oem-right", result.Promoted);
        Assert.Contains("button:oem-right", result.NotPromoted);
        var claw = Assert.Single(record.Buttons, button => button.Name == "Claw");
        Assert.Equal(DeviceKnowledgeSource.LabConfirmed, claw.Provenance!.Source);
        Assert.Contains("report.wsgmlab: segments/buttons/oem-left/attempt-1", claw.Provenance.Reference,
            StringComparison.Ordinal);
        var quick = Assert.Single(record.Buttons, button => button.Name == "Quick Settings");
        Assert.Equal(DeviceKnowledgeSource.WsgmPlugin, quick.Provenance!.Source);
        Assert.Equal(DeviceKnowledgeSource.LabConfirmed, record.Motion!.Provenance!.Source);
        Assert.Throws<IOException>(() => LabPromote.Run(report, output, [], Boundaries(temporary)));
    }

    [Fact]
    public void Promote_NamedDisagreement_ReplacesTheBelief()
    {
        using TemporaryDirectory temporary = new();
        var report = Report(temporary);
        var output = Path.Combine(temporary.Root, "record.json");

        var result = LabPromote.Run(report, output, ["button:oem-right"], Boundaries(temporary));

        Assert.Equal(["button:oem-right"], result.Promoted);
        var record = DeviceKnowledgeBase.Parse(File.ReadAllText(output));
        var quick = Assert.Single(record.Buttons, button => button.Name == "Quick Settings");
        Assert.Equal(DeviceButtonSourceKind.KeyboardChord, quick.Source);
        Assert.Equal(["F15"], quick.PressKeys);
        Assert.Equal(DeviceKnowledgeSource.LabConfirmed, quick.Provenance!.Source);
        Assert.Throws<InvalidDataException>(() =>
            LabPromote.Run(report, Path.Combine(temporary.Root, "other.json"), ["button:none"], Boundaries(temporary)));
        Assert.Throws<InvalidDataException>(() =>
            LabPromote.Run(report, Path.Combine(temporary.Root, "other.json"), ["sleep"], Boundaries(temporary)));
    }

    [Fact]
    public void Promote_WithoutARecord_BuildsANewCuratedRecord()
    {
        using TemporaryDirectory temporary = new();
        var report = Report(temporary, false);
        var output = Path.Combine(temporary.Root, "record.json");

        var result = LabPromote.Run(report, output, [], Boundaries(temporary));

        var record = DeviceKnowledgeBase.Parse(File.ReadAllText(output));
        Assert.Equal("wsgm.test-handheld-th1", result.RecordId);
        Assert.Equal(DeviceKnowledgeStatus.Curated, record.Status);
        var rule = Assert.Single(record.Identity);
        Assert.Equal("MS-1T52", rule.BaseboardProduct);
        Assert.Contains(record.Buttons, button => button is { WizardButton: "OemLeft", Source: DeviceButtonSourceKind.WmiEvent });
    }

    [Fact]
    public void Scaffold_FromLabReport_PrefillsIdentityButtonsMapsAndCapabilities()
    {
        using TemporaryDirectory temporary = new();
        var report = Report(temporary);

        var result = ScaffoldFromLabProjectWorkflow.Run(report, Path.Combine(temporary.Root, "plugin"),
            Boundaries(temporary));

        var directory = result.Scaffold.OutputDirectory;
        Assert.Contains("DeviceProfile.cs", result.Scaffold.Files);
        var profile = File.ReadAllText(Path.Combine(directory, "DeviceProfile.cs"));
        Assert.Contains("BaseboardProduct = \"MS-1T52\"", profile, StringComparison.Ordinal);
        Assert.Contains("DeviceButtonSource.WmiEvent", profile, StringComparison.Ordinal);
        Assert.Contains("CapabilityRole.PowerSustainedLimit", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", profile, StringComparison.Ordinal);
        var manifest = PluginManifestReader.Read(File.ReadAllBytes(Path.Combine(directory, "plugin.wsgm.json")));
        Assert.True(manifest.IsValid);
        Assert.Equal("MS-1T52", Assert.Single(manifest.Manifest!.Hardware).BaseboardProduct);
        Assert.Contains(WSGM.Device.Sdk.Capabilities.CapabilityRole.HapticSink, manifest.Manifest.Capabilities);
        Assert.Equal("0DB0", result.Scaffold.Identity.UsbVendorId);
    }

    [Fact]
    public void FromDeviceFrame_InvertsToDeviceFrame()
    {
        var record = DeviceKnowledgeBase.Default.Records.Single(item => item.Id == ClawRecord);
        var map = record.Motion!.Gyrometer!;

        var roundTrip = LabReview.FromDeviceFrame(LabMotionAnalysis.ToDeviceFrame(map));

        Assert.Equal(map.Swap.OrderBy(pair => pair.Key), roundTrip.Swap.OrderBy(pair => pair.Key));
        Assert.Equal(map.Sign.OrderBy(pair => pair.Key), roundTrip.Sign.OrderBy(pair => pair.Key));
    }

    private static LabReviewItem Item(LabReviewResult review, string field)
    {
        return Assert.Single(review.Items, item => item.Field == field);
    }

    // A Claw project: identity, four button steps, motion, rumble, power and a failed sleep, exported
    // through the real redacting export.
    private static string Report(TemporaryDirectory temporary, bool known = true)
    {
        var project = LabProject.Create(Path.Combine(temporary.Root, "project"), LabStages.Ids, "1.0.0", Now);
        var record = DeviceKnowledgeBase.Default.Records.Single(item => item.Id == ClawRecord);
        project.SetDevice(known
            ? new LabDeviceIdentity { RecordId = ClawRecord, DisplayName = record.DisplayName }
            : new LabDeviceIdentity { ProductName = "Test Handheld", Model = "TH1" });

        var identity = project.BeginAttempt(LabStages.Identity, Now);
        DurableFile.WriteNewText(Path.Combine(identity, "inventory.json"), DeviceLabJson.Serialize(new MachineInventory
        {
            SchemaVersion = 1,
            CapturedAt = Now,
            Firmware = new FirmwareInventory
            {
                SystemManufacturer = "Micro-Star International Co., Ltd.",
                BaseboardManufacturer = "Micro-Star International Co., Ltd.",
                BaseboardProduct = "MS-1T52",
                SystemSku = "1T52.1",
                BiosVersion = "E1T52IMS.10A"
            },
            UsbInterfaces =
            [
                new UsbInterfaceInventory
                {
                    InstanceId = @"HID\VID_0DB0&PID_1901\1",
                    DeviceClass = "HIDClass",
                    VendorId = "0DB0",
                    ProductId = "1901",
                    DeviceRelease = "0100",
                    Present = true
                },
                new UsbInterfaceInventory
                {
                    InstanceId = @"HID\VID_046D&PID_C52B\1",
                    DeviceClass = "HIDClass",
                    VendorId = "046D",
                    ProductId = "C52B",
                    DeviceRelease = "1211",
                    Present = true
                }
            ]
        }));
        project.Finish(LabStages.Identity, LabSegmentStatus.Completed, null, Now);

        Button(project, "oem-left", "Extra button left of the screen",
            [new LabInputCandidate("wmi", null, null, ["MSI_Event: Active=True; MSIEvt=41"], 12, 1, null)], []);
        Button(project, "oem-right", "Extra button right of the screen", [],
            [new LabKeyPresses("hook", null, "F15", 1, 1, [80], false)]);
        Button(project, "back-left1", "Back button, left",
            [new LabInputCandidate("raw-input", "hid:1", "hid 0DB0:1902 FFF0:0040", ["report 01 byte 7: 00 10"], 9, 2,
                "controller")], []);
        Button(project, "a", "A",
            [new LabInputCandidate("xinput", "xinput:0", "xinput", ["A"], 5, 2, null)], []);

        var motion = project.BeginAttempt(LabStages.Motion, Now);
        var gyro = LabMotionAnalysis.ToDeviceFrame(record.Motion!.Gyrometer!);
        DeviceAxisMap accel = new()
        {
            Swap = new Dictionary<string, string> { ["X"] = "Y", ["Y"] = "X", ["Z"] = "Z" },
            Sign = new Dictionary<string, int> { ["X"] = 1, ["Y"] = 1, ["Z"] = 1 }
        };
        project.WriteEvidence(motion, "motion-summary", new
        {
            Summary = "test",
            Sensors = new object[]
            {
                new { SensorId = "legacy:0", Source = "legacy", Kind = "Gyrometer", Map = gyro, MapClear = true },
                new { SensorId = "legacy:1", Source = "legacy", Kind = "Accelerometer", Map = accel, MapClear = true }
            }
        });
        project.Finish(LabStages.Motion, LabSegmentStatus.Completed, null, Now);

        var rumble = project.BeginAttempt(LabStages.Rumble, Now);
        project.WriteEvidence(rumble, "rumble-routes", new
        {
            Routes = new[] { new { Id = "hid:0", Kind = "hid-output", Name = "Device report", Detail = "0DB0:1902" } },
            Probes = new[] { new { Route = "hid:0", Felt = true } }
        });
        project.WriteEvidence(rumble, "rumble-calibration", new
        {
            Routes = new[] { new { Route = "hid:0", Name = "Device report", Swapped = false } }
        });
        project.Finish(LabStages.Rumble, LabSegmentStatus.Completed, null, Now);

        var power = project.BeginAttempt(LabStages.Power, Now);
        project.WriteEvidence(power, "power-tests", new
        {
            Tests = new[]
            {
                new LabPowerTestResult
                {
                    Feature = "tdp", Transport = "wmi-method", Outcome = "passed", Restored = true, At = Now
                },
                new LabPowerTestResult
                {
                    Feature = "charge-limit", Transport = "wmi-method", Outcome = "failed", Restored = true, At = Now
                }
            }
        });
        project.Finish(LabStages.Power, LabSegmentStatus.Completed, null, Now);

        var sleep = project.BeginAttempt(LabStages.Sleep, Now);
        project.WriteEvidence(sleep, "sleep", new { Missing = new[] { "XInput slot 0" } });
        project.Finish(LabStages.Sleep, LabSegmentStatus.Failed, "Missing after wake: XInput slot 0.", Now);

        var target = Path.Combine(temporary.Root, "report.wsgmlab");
        LabExport.Prepare(project).Write(target, Boundaries(temporary));
        return target;
    }

    private static void Button(LabProject project, string id, string name, LabInputCandidate[] candidates,
        LabKeyPresses[] keys)
    {
        var segment = $"{LabStages.Buttons}/{id}";
        var attempt = project.BeginAttempt(segment, Now);
        project.WriteEvidence(attempt, "candidates", new
        {
            Control = new LabControl(id, name, "Press it.", true),
            Answer = "done",
            Flagged = false,
            Candidates = candidates,
            Keys = keys,
            Analog = Array.Empty<object>()
        });
        project.Finish(segment, LabSegmentStatus.Completed, null, Now);
    }

    private static DeviceLabPathBoundaries Boundaries(TemporaryDirectory temporary)
    {
        return new DeviceLabPathBoundaries
        {
            LiveDataDirectory = Path.Combine(temporary.Root, "live-wsgm"), BroadHomeDirectories = []
        };
    }
}
