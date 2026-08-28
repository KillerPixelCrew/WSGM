using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Inventory;

namespace WSGM.DeviceLab.Tests;

/// <summary>
/// The executable capture contract: imported recipes are inert, raw evidence remains separate from
/// analysis, and a shareable archive is byte-for-byte reproducible and self-verifying.
/// </summary>
public class CaptureSchemaTests
{
    [Fact]
    public void StreamEvent_RoundTripsEveryOrderingTimingPayloadAndFailureField()
    {
        CaptureStreamEvent original = Event() with
        {
            SourceTime = new CaptureSourceTimestamp
            {
                Value = 1240,
                Frequency = 1000,
                ClockId = "hid-device-clock",
            },
            ClockSegment = 3,
            DeviceGeneration = 7,
            Loss = EventLossState.SequenceGap,
            Discontinuity = EventDiscontinuity.SuspendResume,
            TimedOut = true,
            Access = EventAccessState.AccessDenied,
        };

        string json = JsonSerializer.Serialize(
            original,
            DeviceLabJsonContext.Default.CaptureStreamEvent);
        CaptureStreamEvent? restored = JsonSerializer.Deserialize(
            json,
            DeviceLabJsonContext.Default.CaptureStreamEvent);

        Assert.NotNull(restored);
        Assert.Equal(original.EventId, restored.EventId);
        Assert.Equal(original.SourceId, restored.SourceId);
        Assert.Equal(original.RecipeStepId, restored.RecipeStepId);
        Assert.Equal(original.SourceSequence, restored.SourceSequence);
        Assert.Equal(original.GlobalSequence, restored.GlobalSequence);
        Assert.Equal(original.QpcReceiptTime, restored.QpcReceiptTime);
        Assert.Equal(original.SourceTime, restored.SourceTime);
        Assert.Equal(original.ClockSegment, restored.ClockSegment);
        Assert.Equal(original.DeviceGeneration, restored.DeviceGeneration);
        Assert.Equal(original.Payload.Bytes, restored.Payload.Bytes);
        Assert.Equal(original.Payload.Length, restored.Payload.Length);
        Assert.Equal(original.Loss, restored.Loss);
        Assert.Equal(original.Discontinuity, restored.Discontinuity);
        Assert.Equal(original.TimedOut, restored.TimedOut);
        Assert.Equal(original.Access, restored.Access);
    }

    [Fact]
    public void IncludedPayload_RequiresExactLengthBytesAndHash()
    {
        SanitizedCaptureBundle bundle = Bundle() with
        {
            Streams =
            [
                new CaptureStreamFile
                {
                    SourceId = "hid.input",
                    Events =
                    [
                        Event() with
                        {
                            Payload = new CapturedPayload
                            {
                                Length = 3,
                                Disposition = PayloadDisposition.Included,
                                Bytes = [0x01, 0x02],
                                Sha256 = CaptureHashFile.Hash([0x01, 0x02]),
                            },
                        },
                    ],
                },
            ],
        };

        IReadOnlyList<CaptureValidationError> errors = CaptureSchemaValidator.Validate(bundle);

        Assert.Contains(errors, error =>
            error.Path == "event-0001"
            && error.Message.Contains("reported length", StringComparison.Ordinal));
    }

    [Fact]
    public void OmittedPayload_CannotSmuggleBytesOrAHash()
    {
        SanitizedCaptureBundle bundle = Bundle() with
        {
            Streams =
            [
                new CaptureStreamFile
                {
                    SourceId = "hid.input",
                    Events =
                    [
                        Event() with
                        {
                            Payload = new CapturedPayload
                            {
                                Length = 2,
                                Disposition = PayloadDisposition.Redacted,
                                Bytes = [0x01, 0x02],
                                Sha256 = CaptureHashFile.Hash([0x01, 0x02]),
                            },
                        },
                    ],
                },
            ],
        };

        Assert.Contains(CaptureSchemaValidator.Validate(bundle), error =>
            error.Message.Contains("cannot retain bytes", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportedRecipe_IsSerializedAsInertEvidenceWithAClosedObserveOnlyKind()
    {
        ObserveOnlyRecipe recipe = Bundle().Recipe;

        string json = JsonSerializer.Serialize(
            recipe,
            DeviceLabJsonContext.Default.ObserveOnlyRecipe);

        Assert.Contains("\"authority\": \"InertEvidence\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"HidInput\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("write", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DerivedAnalysis_MustLinkBackToRawEvidence()
    {
        SanitizedCaptureBundle bundle = Bundle() with
        {
            Analysis =
            [
                new CaptureAnalysisFile
                {
                    AnalyzerId = "hid-diff",
                    Results =
                    [
                        Analysis() with { SupportingEventIds = [] },
                    ],
                },
            ],
        };

        Assert.Contains(CaptureSchemaValidator.Validate(bundle), error =>
            error.Path == "analysis-0001"
            && error.Message.Contains("raw event", StringComparison.Ordinal));
    }

    [Fact]
    public void ShareableBundle_RequiresTheShareablePrivacyAndRedactionMarkers()
    {
        SanitizedCaptureBundle bundle = Bundle() with
        {
            Manifest = Bundle().Manifest with { Privacy = CapturePrivacy.PrivateWorking },
            Redaction = Bundle().Redaction with { DefaultRedactionApplied = false },
        };

        IReadOnlyList<CaptureValidationError> errors = CaptureSchemaValidator.Validate(bundle);

        Assert.Contains(errors, error => error.Path == "manifest.privacy");
        Assert.Contains(errors, error => error.Path == "redaction.defaultRedactionApplied");
    }

    [Fact]
    public void ArchivePaths_CannotTraverseOrUseWindowsAliases()
    {
        Assert.False(CaptureBundleLayout.IsSafeRelativePath("streams/../config.json"));
        Assert.False(CaptureBundleLayout.IsSafeRelativePath(@"streams\hid.ndjson"));
        Assert.False(CaptureBundleLayout.IsSafeRelativePath("C:/Users/operator/config.json"));
        Assert.False(CaptureBundleLayout.IsSafeRelativePath("/absolute/path"));
        Assert.True(CaptureBundleLayout.IsSafeRelativePath("streams/hid.ndjson"));
    }

    [Fact]
    public void BundleWriter_ProducesDeterministicLayoutNdjsonAndVerifiedHashes()
    {
        SanitizedCaptureBundle bundle = Bundle();
        using MemoryStream first = new();
        using MemoryStream second = new();

        CaptureBundleWriter.Write(first, bundle);
        CaptureBundleWriter.Write(second, bundle);

        Assert.Equal(first.ToArray(), second.ToArray());

        first.Position = 0;
        using ZipArchive archive = new(first, ZipArchiveMode.Read, leaveOpen: true);
        string[] paths = [.. archive.Entries.Select(entry => entry.FullName)];
        Assert.Equal(
        [
            "analysis/hid-diff.ndjson",
            "claims.json",
            "hashes.sha256",
            "inventory.json",
            "manifest.json",
            "recipe.json",
            "redaction.json",
            "streams/hid-input.ndjson",
        ], paths);

        string ndjson = ReadText(archive, "streams/hid-input.ndjson");
        Assert.Single(ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using (JsonDocument.Parse(ndjson))
        {
            // Parsing the one compact line proves the stream is NDJSON, not indented multi-line JSON.
        }

        Dictionary<string, string> hashes = ReadText(archive, "hashes.sha256")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);

        Assert.DoesNotContain("hashes.sha256", hashes.Keys);
        foreach (ZipArchiveEntry entry in archive.Entries.Where(entry => entry.FullName != "hashes.sha256"))
        {
            using Stream input = entry.Open();
            string actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            Assert.Equal(hashes[entry.FullName], actual);
        }
    }

    [Fact]
    public void BundleWriter_RefusesAnInvalidBundleBeforeWritingAnything()
    {
        SanitizedCaptureBundle bundle = Bundle() with
        {
            Manifest = Bundle().Manifest with
            {
                Streams =
                [
                    new CaptureStreamDescriptor
                    {
                        SourceId = "hid.input",
                        Path = "streams/../live-config.json",
                        EventCount = 1,
                    },
                ],
            },
        };
        using MemoryStream output = new();

        Assert.Throws<InvalidDataException>(() => CaptureBundleWriter.Write(output, bundle));
        Assert.Equal(0, output.Length);
    }

    private static string ReadText(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = Assert.Single(archive.Entries, entry => entry.FullName == path);
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static SanitizedCaptureBundle Bundle()
    {
        CaptureStreamEvent captureEvent = Event();
        CaptureAnalysisResult analysis = Analysis();

        return new SanitizedCaptureBundle
        {
            Manifest = new ShareableCaptureManifest
            {
                SchemaVersion = CaptureSchema.ShareableManifestVersion,
                BundleId = "capture-reference-0001",
                ToolVersion = "wsgm-device@2.0-test",
                StartedAt = new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2026, 8, 28, 10, 0, 1, TimeSpan.Zero),
                QpcFrequency = 10_000_000,
                Streams =
                [
                    new CaptureStreamDescriptor
                    {
                        SourceId = "hid.input",
                        Path = "streams/hid-input.ndjson",
                        EventCount = 1,
                    },
                ],
                Analysis =
                [
                    new CaptureAnalysisDescriptor
                    {
                        AnalyzerId = "hid-diff",
                        AnalyzerVersion = "1.0.0",
                        Path = "analysis/hid-diff.ndjson",
                        ResultCount = 1,
                    },
                ],
            },
            Recipe = new ObserveOnlyRecipe
            {
                SchemaVersion = CaptureSchema.RecipeVersion,
                RecipeId = "reference-hid-observation",
                DisplayName = "Reference HID observation",
                Steps =
                [
                    new ObservationStep
                    {
                        StepId = "press-a",
                        SourceId = "hid.input",
                        Kind = ObservationStepKind.HidInput,
                        OperatorPrompt = "Press and release A once.",
                        DurationMilliseconds = 1000,
                    },
                ],
            },
            Inventory = new MachineInventory
            {
                SchemaVersion = WindowsInventoryCollector.CurrentSchemaVersion,
                Firmware = new FirmwareInventory
                {
                    SystemManufacturer = "Micro-Star International Co., Ltd.",
                    BaseboardProduct = "MS-1T52",
                },
                CapturedAt = new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero),
            },
            Streams =
            [
                new CaptureStreamFile
                {
                    SourceId = "hid.input",
                    Events = [captureEvent],
                },
            ],
            Analysis =
            [
                new CaptureAnalysisFile
                {
                    AnalyzerId = "hid-diff",
                    Results = [analysis],
                },
            ],
            Redaction = new CaptureRedactionManifest
            {
                SchemaVersion = CaptureSchema.RedactionVersion,
                DefaultRedactionApplied = true,
            },
        };
    }

    private static CaptureStreamEvent Event()
    {
        byte[] bytes = [0x01, 0x00, 0x80, 0x7f];
        return new CaptureStreamEvent
        {
            SchemaVersion = CaptureSchema.StreamEventVersion,
            EventId = "event-0001",
            SourceId = "hid.input",
            RecipeStepId = "press-a",
            SourceSequence = 1,
            GlobalSequence = 1,
            QpcReceiptTime = 10_000,
            ClockSegment = 0,
            DeviceGeneration = 1,
            Payload = new CapturedPayload
            {
                Length = bytes.Length,
                Disposition = PayloadDisposition.Included,
                Bytes = bytes,
                Sha256 = CaptureHashFile.Hash(bytes),
            },
            Loss = EventLossState.None,
            Discontinuity = EventDiscontinuity.None,
            TimedOut = false,
            Access = EventAccessState.Available,
        };
    }

    private static CaptureAnalysisResult Analysis() => new()
    {
        SchemaVersion = CaptureSchema.AnalysisResultVersion,
        ResultId = "analysis-0001",
        AnalyzerId = "hid-diff",
        AnalyzerVersion = "1.0.0",
        Meaning = "Byte 2 changed while A was pressed.",
        Values =
        [
            new CaptureAnalysisValue { Key = "offset", Value = "2", Unit = "byte" },
        ],
        SupportingEventIds = ["event-0001"],
        Limitations = ["Timing correlation is a candidate, not proof of causality."],
    };
}
