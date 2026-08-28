using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Contracts.Ipc;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Evidence;
using WSGM.DeviceLab.Core.Preflight;
using WSGM.DeviceLab.Core.Scaffolding;

namespace WSGM.DeviceLab.Tests;

/// <summary>Scaffolding emits exact, evidence-pinned, unavailable-by-default developer projects.</summary>
public class ScaffoldGeneratorTests
{
    [Fact]
    public void Generator_EmitsCompleteScaffoldWithoutInventingUnverifiedImplementation()
    {
        ScaffoldGenerationPlan plan = DevicePluginScaffoldGenerator.Create(Request());

        string[] paths = [.. plan.Files.Select(file => file.Path)];
        Assert.Contains("plugin.wsgm.json", paths);
        Assert.Contains("evidence.lock.json", paths);
        Assert.Contains("README.md", paths);
        Assert.Contains("BRINGUP.md", paths);
        Assert.Contains("Generated/ExactDetector.g.cs", paths);
        Assert.Contains("Generated/ResourceGraph.g.cs", paths);
        Assert.Contains("Generated/ModuleComposition.g.cs", paths);
        Assert.Contains("Generated/Capabilities.g.cs", paths);
        Assert.Contains("Generated/RecoveryJournal.g.cs", paths);
        Assert.Contains("PluginLifecycle.cs", paths);
        Assert.Contains("tests/GeneratedContractTests.cs", paths);
        Assert.Equal("hardware-evidence-incomplete", plan.UnavailableCapabilities["fan.speed"]);

        string capabilities = plan.Files.Single(file => file.Path == "Generated/Capabilities.g.cs").Content;
        Assert.Contains("Parse_power_primary_limit", capabilities, StringComparison.Ordinal);
        Assert.Contains("\"fan.speed\", \"fan\", false", capabilities, StringComparison.Ordinal);
        Assert.DoesNotContain("Parse_fan_speed", capabilities, StringComparison.Ordinal);
        Assert.DoesNotContain("setter", capabilities, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generator_IsDeterministicAcrossSemanticInputOrdering()
    {
        ScaffoldGenerationRequest forward = Request();
        ScaffoldGenerationRequest reversed = forward with
        {
            Modules = [.. forward.Modules.Reverse()],
            Resources = [.. forward.Resources.Reverse()],
            Capabilities = [.. forward.Capabilities.Reverse()],
            Claims = [.. forward.Claims.Reverse()],
            Identity = forward.Identity with
            {
                FirmwareIdentities = [.. forward.Identity.FirmwareIdentities.Reverse()],
                ProductIds = [.. forward.Identity.ProductIds.Reverse()],
            },
            Input = forward.Input with
            {
                ModuleLocks = [.. forward.Input.ModuleLocks.Reverse()],
                FixtureIds = [.. forward.Input.FixtureIds.Reverse()],
            },
        };

        ScaffoldGenerationPlan first = DevicePluginScaffoldGenerator.Create(forward);
        ScaffoldGenerationPlan second = DevicePluginScaffoldGenerator.Create(reversed);

        Assert.Equal(
            first.Files.Select(file => (file.Path, file.Ownership, file.Content)),
            second.Files.Select(file => (file.Path, file.Ownership, file.Content)));
        Assert.Equal(DeviceLabJson.Serialize(first.Output), DeviceLabJson.Serialize(second.Output));
    }

    [Fact]
    public void Manifest_IsScaffoldedReadOnlyAndStillRequiresExactMachineIdentity()
    {
        ScaffoldGenerationPlan plan = DevicePluginScaffoldGenerator.Create(Request());
        byte[] json = Encoding.UTF8.GetBytes(
            plan.Files.Single(file => file.Path == "plugin.wsgm.json").Content);
        PluginManifestReadResult read = PluginManifestReader.Read(json);

        Assert.NotNull(read.Manifest);
        Assert.Empty(read.Errors);
        Assert.All(read.Manifest.Devices[0].Resources, resource =>
            Assert.Equal(ResourceAccess.Read, resource.Access));
        Assert.Contains(read.Manifest.Devices[0].Identity, identity =>
            identity.Signal == WSGM.Device.Contracts.Identity.IdentitySignal.SmbiosBaseboardProduct
            && identity.Strength == WSGM.Device.Contracts.Identity.IdentityStrength.Required);
        Assert.Contains("Scaffolded / Developer", plan.Files.Single(file => file.Path == "README.md").Content);
    }

    [Fact]
    public void Generator_RejectsAnotherBoardsClaimAndMismatchedModulePin()
    {
        ScaffoldGenerationRequest request = Request();

        Assert.Throws<InvalidDataException>(() => DevicePluginScaffoldGenerator.Create(
            request with
            {
                Claims = [request.Claims[0] with
                {
                    Scope = request.Claims[0].Scope with { BaseboardProduct = "other-board" },
                }, request.Claims[1]],
            }));
        Assert.Throws<InvalidDataException>(() => DevicePluginScaffoldGenerator.Create(
            request with
            {
                Modules = [request.Modules[0] with { Version = 99 }, request.Modules[1]],
            }));
    }

    [Fact]
    public void Regeneration_RequiresReviewForFixtureOrWeakenedEvidenceAndNeverOwnsHandwrittenFiles()
    {
        ScaffoldGenerationRequest initialRequest = Request();
        ScaffoldGenerationPlan initial = DevicePluginScaffoldGenerator.Create(initialRequest);
        EvidenceClaim weakened = initialRequest.Claims[0] with { State = ClaimState.Correlated };
        ScaffoldGenerationRequest weakenedRequest = WithRebuiltInput(
            initialRequest with { Claims = [weakened, initialRequest.Claims[1]] });
        ScaffoldGenerationPlan weakenedPlan = DevicePluginScaffoldGenerator.Create(weakenedRequest);
        ScaffoldRegenerationReview review = DevicePluginScaffoldRegeneration.Compare(initial, weakenedPlan);

        Assert.True(review.RequiresExplicitReview);
        Assert.Contains(review.EvidenceChanges, change =>
            change.Kind is EvidenceChangeKind.ClaimWeakened);
        Assert.All(initial.Files.Where(file => file.Ownership is ScaffoldFileOwnership.HandwrittenTemplate), file =>
            Assert.DoesNotContain(file.Path, review.GeneratedFileChanges));

        ScaffoldGenerationRequest fixtureRequest = WithRebuiltInput(initialRequest with
        {
            Input = initialRequest.Input with { FixtureIds = ["fixture-a", "fixture-new"] },
        });
        Assert.True(DevicePluginScaffoldRegeneration.Compare(
            initial,
            DevicePluginScaffoldGenerator.Create(fixtureRequest)).RequiresExplicitReview);
    }

    [Fact]
    public void Writer_UsesOneNewExplicitDirectoryAndRefusesRegenerationOverwrite()
    {
        using TestDirectory temp = new();
        ScaffoldGenerationPlan plan = DevicePluginScaffoldGenerator.Create(Request());
        string output = Path.Combine(temp.Path, "generated-plugin");

        DevicePluginScaffoldWriter.Write(plan, output, temp.Boundaries());

        Assert.True(File.Exists(Path.Combine(output, "plugin.wsgm.json")));
        Assert.True(File.Exists(Path.Combine(output, "scaffold-output.json")));
        foreach (ScaffoldOutputFile file in plan.Output.Files)
        {
            string path = Path.Combine(output, file.Path.Replace('/', Path.DirectorySeparatorChar));
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Assert.Equal(file.Sha256, actual);
        }

        Assert.Throws<IOException>(() =>
            DevicePluginScaffoldWriter.Write(plan, output, temp.Boundaries()));
    }

    [Fact]
    public void GeneratedProjectCarriesOfflineDetectionReplayAvailabilityAndCommandIntentTests()
    {
        ScaffoldGenerationPlan plan = DevicePluginScaffoldGenerator.Create(Request());
        string tests = plan.Files.Single(file => file.Path == "tests/GeneratedContractTests.cs").Content;
        string project = plan.Files.Single(file => file.Path.EndsWith(".Tests.csproj", StringComparison.Ordinal)).Content;

        Assert.Contains("Detection_RequiresExactBoardAndKnownFirmware", tests, StringComparison.Ordinal);
        Assert.Contains("EndpointBinding_RequiresExactEndpointVidAndPid", tests, StringComparison.Ordinal);
        Assert.Contains("CaptureReplay_UsesOnlyGeneratedVerifiedParserWhenPresent", tests, StringComparison.Ordinal);
        Assert.Contains("CapabilitySnapshot_PreservesUnavailableReason", tests, StringComparison.Ordinal);
        Assert.Contains("CommandIntent_HasNoGeneratedHardwareSetter", tests, StringComparison.Ordinal);
        Assert.Contains("IsTestProject", project, StringComparison.Ordinal);
    }

    private static ScaffoldGenerationRequest Request()
    {
        EvidenceClaim power = Claim("claim.power", ClaimState.HardwareVerified, offset: 1);
        EvidenceClaim fan = Claim("claim.fan", ClaimState.Correlated, offset: 2);
        ScaffoldModuleSelection[] modules =
        [
            new ScaffoldModuleSelection
            {
                ModuleId = "vendor-wmi",
                Version = 1,
                Layer = ModuleLayer.Transport,
            },
            new ScaffoldModuleSelection
            {
                ModuleId = "device-layout",
                Version = 2,
                Layer = ModuleLayer.Layout,
            },
        ];
        EvidenceLock evidenceLock = EvidenceLockBuilder.Build(
            "exact-device",
            DevicePluginScaffoldGenerator.GeneratorVersion,
            [power, fan],
            [.. modules.Select(module => (module.ModuleId, module.Version))]);
        string evidenceHash = Hash(DeviceLabJson.Serialize(evidenceLock));
        ScaffoldInputManifest input = new()
        {
            SchemaVersion = ScaffoldSchema.CurrentVersion,
            DeviceDefinitionId = "exact-device",
            SourceCaptureSha256 = new string('1', 64),
            GeneratorVersion = DevicePluginScaffoldGenerator.GeneratorVersion,
            RuntimeApi = new ScaffoldRuntimeApi
            {
                MinimumVersion = DeviceProtocol.MinSupportedVersion,
                MaximumVersion = DeviceProtocol.MaxSupportedVersion,
                NegotiatedVersion = DeviceProtocol.MaxSupportedVersion,
                SchemaFingerprint = DeviceProtocol.SchemaFingerprint,
            },
            ModuleLocks = [.. modules.Select(module => new PinnedModule(module.ModuleId, module.Version))],
            EvidenceLock = new ScaffoldEvidenceLockReference { Sha256 = evidenceHash },
            FixtureIds = ["fixture-b", "fixture-a"],
        };

        return new ScaffoldGenerationRequest
        {
            Input = input,
            PackageId = "wsgm.device.generated-test",
            RootNamespace = "WSGM.Device.GeneratedTest",
            DisplayName = "Generated Test Device",
            Publisher = "WSGM Device Lab",
            Identity = new ScaffoldExactIdentity
            {
                SystemManufacturer = "Vendor, Inc.",
                BaseboardProduct = "BOARD-1",
                FirmwareIdentities = ["FW-2", "FW-1"],
                EndpointId = "control",
                EndpointRole = "control",
                VendorId = "1234",
                ProductIds = ["5679", "5678"],
            },
            Modules = modules,
            Resources =
            [
                new ScaffoldResourceSelection
                {
                    ResourceId = "power",
                    Kind = ResourceKind.Wmi,
                    RequestedAccess = ResourceAccess.ReadWrite,
                    EndpointId = "control",
                    RecoveryJournalFields = ["planned-pair", "original-pair"],
                },
                new ScaffoldResourceSelection
                {
                    ResourceId = "fan",
                    Kind = ResourceKind.Wmi,
                    RequestedAccess = ResourceAccess.ReadWrite,
                    EndpointId = "control",
                    RecoveryJournalFields = ["firmware-mode", "original-duty"],
                },
            ],
            Capabilities =
            [
                new ScaffoldCapabilitySelection
                {
                    CapabilityId = "power.primary-limit",
                    ResourceId = "power",
                    RequiredClaimIds = [power.ClaimId],
                    WriteEligibility = WriteEligibility.Production,
                    GenerateParser = true,
                },
                new ScaffoldCapabilitySelection
                {
                    CapabilityId = "fan.speed",
                    ResourceId = "fan",
                    RequiredClaimIds = [fan.ClaimId],
                    WriteEligibility = WriteEligibility.ReadOnly,
                    GenerateParser = true,
                },
            ],
            Claims = [power, fan],
        };
    }

    private static ScaffoldGenerationRequest WithRebuiltInput(ScaffoldGenerationRequest request)
    {
        EvidenceLock evidenceLock = EvidenceLockBuilder.Build(
            request.Input.DeviceDefinitionId,
            request.Input.GeneratorVersion,
            request.Claims,
            [.. request.Modules.Select(module => (module.ModuleId, module.Version))]);
        return request with
        {
            Input = request.Input with
            {
                EvidenceLock = new ScaffoldEvidenceLockReference
                {
                    Sha256 = Hash(DeviceLabJson.Serialize(evidenceLock)),
                },
            },
        };
    }

    private static EvidenceClaim Claim(string id, ClaimState state, int offset) => new()
    {
        ClaimId = id,
        Scope = new ClaimScope
        {
            BaseboardProduct = "BOARD-1",
            BiosVersion = "FW-1",
        },
        Transport = "vendor-wmi",
        Endpoint = "control",
        Selector = "GetStatus",
        Offset = offset,
        WidthBits = 8,
        Mask = 0xff,
        ProposedMeaning = id,
        State = state,
        Repetitions = 3,
        Restoration = RestorationResult.NotApplicable,
        Provenance = new ClaimProvenance
        {
            Source = "capture",
            Kind = ProvenanceKind.IndependentCapture,
        },
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());

        public TestDirectory()
        {
            Path = System.IO.Path.Combine(_tempRoot, $"wsgm-scaffold-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public DeviceLabPathBoundaries Boundaries() => new()
        {
            LiveDataDirectory = System.IO.Path.Combine(_tempRoot, "never-live-wsgm"),
            RepositoryRoot = System.IO.Path.Combine(_tempRoot, "never-repository"),
            BroadHomeDirectories = [],
        };

        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Test cleanup escaped the system temporary directory.");
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
