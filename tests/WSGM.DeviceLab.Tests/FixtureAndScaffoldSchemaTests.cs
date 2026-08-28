using System.Text.Json;
using WSGM.Device.Contracts.Ipc;
using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Fixtures;
using WSGM.DeviceLab.Core.Scaffolding;

namespace WSGM.DeviceLab.Tests;

/// <summary>
/// Fixture and scaffold schemas keep replay away from hardware and regeneration away from
/// developer-owned files.
/// </summary>
public class FixtureAndScaffoldSchemaTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void FixturePolicy_HasOnlyTheSimulatorOnlyValue()
    {
        FixtureReplayPolicy policy = Assert.Single(Enum.GetValues<FixtureReplayPolicy>());

        Assert.Equal(FixtureReplayPolicy.SimulatorOnly, policy);
    }

    [Fact]
    public void FixtureArtifacts_MustStayInPlainInputAndExpectedDirectories()
    {
        FixtureManifest manifest = Fixture() with
        {
            Inputs =
            [
                new FixtureArtifact
                {
                    Path = "../private-capture/event.bin",
                    MediaType = "application/octet-stream",
                    Length = 4,
                    Sha256 = Hash,
                },
            ],
        };

        Assert.Contains(FixtureSchemaValidator.Validate(manifest), error =>
            error.Message.Contains("input/", StringComparison.Ordinal));
    }

    [Fact]
    public void FixtureManifest_RoundTripsDeterministically()
    {
        FixtureManifest manifest = Fixture();

        string first = JsonSerializer.Serialize(manifest, DeviceLabJsonContext.Default.FixtureManifest);
        FixtureManifest? restored = JsonSerializer.Deserialize(
            first,
            DeviceLabJsonContext.Default.FixtureManifest);
        string second = JsonSerializer.Serialize(restored, DeviceLabJsonContext.Default.FixtureManifest);

        Assert.Empty(FixtureSchemaValidator.Validate(manifest));
        Assert.Equal(first, second);
    }

    [Fact]
    public void ScaffoldInput_PinsRuntimeModulesEvidenceAndFixtures()
    {
        ScaffoldInputManifest input = Input();

        Assert.Empty(ScaffoldSchemaValidator.Validate(input));
        Assert.Equal(DeviceProtocol.SchemaFingerprint, input.RuntimeApi.SchemaFingerprint);
        Assert.Equal("msi-wmi", Assert.Single(input.ModuleLocks).ModuleId);
        Assert.Equal("evidence.lock.json", input.EvidenceLock.Path);
        Assert.Equal("reference-hid", Assert.Single(input.FixtureIds));
    }

    [Fact]
    public void ScaffoldOutput_SeparatesGeneratedAndDeveloperOwnedFiles()
    {
        ScaffoldOutputManifest output = Output();

        Assert.Empty(ScaffoldSchemaValidator.Validate(output));
        Assert.Contains(output.Files, file =>
            file.Ownership is ScaffoldFileOwnership.Generated
            && file.OwnershipMarker == ScaffoldSchema.GeneratedMarker);
        Assert.Contains(output.Files, file =>
            file.Ownership is ScaffoldFileOwnership.HandwrittenTemplate
            && file.OwnershipMarker == ScaffoldSchema.HandwrittenTemplateMarker);
    }

    [Fact]
    public void HandwrittenGeneratedFile_IsRejectedBeforeRegenerationCanOverwriteIt()
    {
        ScaffoldOutputManifest output = Output() with
        {
            Files =
            [
                new ScaffoldOutputFile
                {
                    Path = "src/Generated/DeviceFingerprint.g.cs",
                    Ownership = ScaffoldFileOwnership.HandwrittenTemplate,
                    OwnershipMarker = ScaffoldSchema.HandwrittenTemplateMarker,
                    Sha256 = Hash,
                },
            ],
        };

        Assert.Contains(ScaffoldSchemaValidator.Validate(output), error =>
            error.Message.Contains(".g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratorCannotClaimSupportedOrTrustedStatus()
    {
        ScaffoldStatus status = Assert.Single(Enum.GetValues<ScaffoldStatus>());

        Assert.Equal(ScaffoldStatus.Scaffolded, status);
    }

    private static FixtureManifest Fixture() => new()
    {
        SchemaVersion = FixtureSchema.CurrentVersion,
        FixtureId = "reference-hid",
        SourceCaptureSha256 = Hash,
        ExtractorVersion = "wsgm-device@2.0-test",
        Inputs =
        [
            new FixtureArtifact
            {
                Path = "input/events.ndjson",
                MediaType = "application/x-ndjson",
                Length = 128,
                Sha256 = Hash,
            },
        ],
        ExpectedOutputs =
        [
            new FixtureArtifact
            {
                Path = "expected/controller-state.json",
                MediaType = "application/json",
                Length = 64,
                Sha256 = Hash,
            },
        ],
        ClaimIds = ["claim.a-button"],
    };

    private static ScaffoldInputManifest Input() => new()
    {
        SchemaVersion = ScaffoldSchema.CurrentVersion,
        DeviceDefinitionId = "msi-claw-8-a2vm",
        SourceCaptureSha256 = Hash,
        GeneratorVersion = "wsgm-scaffold@1",
        RuntimeApi = Runtime(),
        ModuleLocks = [new("msi-wmi", 1)],
        EvidenceLock = new ScaffoldEvidenceLockReference { Sha256 = Hash },
        FixtureIds = ["reference-hid"],
    };

    private static ScaffoldOutputManifest Output() => new()
    {
        SchemaVersion = ScaffoldSchema.CurrentVersion,
        InputSha256 = Hash,
        GeneratorVersion = "wsgm-scaffold@1",
        RuntimeApi = Runtime(),
        Files =
        [
            new ScaffoldOutputFile
            {
                Path = "src/Generated/DeviceFingerprint.g.cs",
                Ownership = ScaffoldFileOwnership.Generated,
                OwnershipMarker = ScaffoldSchema.GeneratedMarker,
                Sha256 = Hash,
            },
            new ScaffoldOutputFile
            {
                Path = "src/DevicePlugin.cs",
                Ownership = ScaffoldFileOwnership.HandwrittenTemplate,
                OwnershipMarker = ScaffoldSchema.HandwrittenTemplateMarker,
                Sha256 = Hash,
            },
        ],
    };

    private static ScaffoldRuntimeApi Runtime() => new()
    {
        MinimumVersion = DeviceProtocol.MinSupportedVersion,
        MaximumVersion = DeviceProtocol.MaxSupportedVersion,
        NegotiatedVersion = DeviceProtocol.MaxSupportedVersion,
        SchemaFingerprint = DeviceProtocol.SchemaFingerprint,
    };
}
