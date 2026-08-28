using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of the package rules, exercised against constructed manifests so
/// each rule fails in isolation.
/// </summary>
public class PluginManifestValidatorTests
{
    [Fact]
    public void Validate_AMinimalWellFormedManifest_HasNoErrors()
    {
        Assert.Empty(PluginManifestValidator.Validate(Minimal()));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    [InlineData("has:colon")]
    public void Validate_AnIdentifierWithAPathOrSeparatorCharacter_IsRejected(string id)
    {
        // Identifiers reach diagnostics, log lines, and directory names, so a separator smuggled
        // into one is a path expression waiting to be resolved somewhere downstream.
        IReadOnlyList<ManifestValidationError> errors =
            PluginManifestValidator.Validate(Minimal() with { Id = id });

        Assert.Contains(errors, e => e.Code == ManifestValidationCode.InvalidIdentifier);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("sub/../../escape.dll")]
    [InlineData("/rooted.dll")]
    [InlineData("C:\\absolute.dll")]
    [InlineData("\\\\server\\share\\payload.dll")]
    [InlineData("./relative.dll")]
    public void Validate_AnEntryPointThatCanEscapeThePackageDirectory_IsRejected(string entryPoint)
    {
        IReadOnlyList<ManifestValidationError> errors =
            PluginManifestValidator.Validate(Minimal() with { EntryPoint = entryPoint });

        Assert.Contains(errors, e => e.Code == ManifestValidationCode.UnsafePath);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x")]
    [InlineData("1.-2")]
    [InlineData("")]
    public void Validate_AVersionThatIsNotDottedNumeric_IsRejected(string version)
    {
        IReadOnlyList<ManifestValidationError> errors =
            PluginManifestValidator.Validate(Minimal() with { Version = version });

        Assert.Contains(errors, e =>
            e.Code is ManifestValidationCode.InvalidVersion or ManifestValidationCode.MissingField);
    }

    [Fact]
    public void Validate_DuplicateDeviceIds_AreRejected()
    {
        PluginManifest manifest = Minimal() with
        {
            Devices = [MinimalDevice(), MinimalDevice()],
        };

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.DuplicateIdentifier);
    }

    [Fact]
    public void Validate_TheSameModuleComposedTwice_IsRejected()
    {
        // Two versions of one module would make the effective layout, limits, and recovery policy
        // depend on load order.
        PluginManifest manifest = WithDevice(MinimalDevice() with
        {
            Modules =
            [
                new ModuleReference { Id = "MsiClawMcu", Version = 1, Layer = ModuleLayer.Protocol },
                new ModuleReference { Id = "MsiClawMcu", Version = 2, Layer = ModuleLayer.Protocol },
            ],
        });

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.DuplicateIdentifier);
    }

    [Fact]
    public void Validate_AnUnpinnedModuleVersion_IsRejected()
    {
        PluginManifest manifest = WithDevice(MinimalDevice() with
        {
            Modules = [new ModuleReference { Id = "MsiClawMcu", Version = 0, Layer = ModuleLayer.Protocol }],
        });

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.InvalidVersion);
    }

    [Fact]
    public void Validate_AResourceBoundToAnUndeclaredEndpoint_IsRejected()
    {
        PluginManifest manifest = WithDevice(MinimalDevice() with
        {
            Resources =
            [
                new ResourceDeclaration
                {
                    Id = "claw-mcu",
                    Kind = ResourceKind.Hid,
                    Access = ResourceAccess.ReadWrite,
                    EndpointId = "not-declared",
                },
            ],
        });

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.UnresolvedReference);
    }

    [Theory]
    [InlineData("db0")]
    [InlineData("0xDB0")]
    [InlineData("0DB")]
    [InlineData("0DB0 ")]
    [InlineData("0db0")]
    public void Validate_AUsbIdentifierNotInCanonicalForm_IsRejected(string vendorId)
    {
        // One canonical form keeps comparison ordinal. Accepting several spellings is how a
        // manifest ends up near-missing a match for a reason nobody can see in review.
        PluginManifest manifest = WithDevice(MinimalDevice() with
        {
            UsbEndpoints =
            [
                new UsbEndpointDeclaration
                {
                    Id = "gamepad",
                    Role = "controller",
                    VendorId = vendorId,
                    ProductIds = ["1901"],
                },
            ],
        });

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.InvalidHexIdentifier);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ManifestLimits.MaxObservationWeight + 1)]
    public void Validate_AWeightOutsideTheAllowedRange_IsRejected(int weight)
    {
        PluginManifest manifest = WithDevice(MinimalDevice() with
        {
            Identity =
            [
                RequiredBoard(),
                new IdentityObservation
                {
                    Signal = IdentitySignal.CpuIdentity,
                    Strength = IdentityStrength.Weighted,
                    Weight = weight,
                    Values = ["6-189-1"],
                },
            ],
        });

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.InvalidObservationWeight);
    }

    [Fact]
    public void Validate_AMatchingObservationWithNoValues_IsRejected()
    {
        PluginManifest manifest = WithDevice(MinimalDevice() with
        {
            Identity =
            [
                new IdentityObservation
                {
                    Signal = IdentitySignal.SmbiosBaseboardProduct,
                    Strength = IdentityStrength.Required,
                    Values = [],
                },
            ],
        });

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.MissingObservationValues);
    }

    [Theory]
    [InlineData(IdentityStrength.Weighted)]
    [InlineData(IdentityStrength.Informational)]
    public void Validate_MarketingTextAsAWeakSignal_IsAllowed(IdentityStrength strength)
    {
        // The rule bans marketing text as a *gate*, not as evidence. It remains useful for display
        // and for ordering two candidates that both already passed their hard constraints.
        PluginManifest manifest = WithDevice(MinimalDevice() with
        {
            Identity =
            [
                RequiredBoard(),
                new IdentityObservation
                {
                    Signal = IdentitySignal.SmbiosSystemProduct,
                    Strength = strength,
                    Weight = strength == IdentityStrength.Weighted ? 10 : 0,
                    Values = ["Claw 8 AI+ A2VM"],
                },
            ],
        });

        Assert.DoesNotContain(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.MarketingNameAsHardGate);
    }

    [Theory]
    [InlineData(ProvenanceClass.CopiedCode)]
    [InlineData(ProvenanceClass.RedistributedBinary)]
    public void Validate_ProvenanceThatShipsAnothersExpression_RequiresARecordedApproval(
        ProvenanceClass provenanceClass)
    {
        PluginManifest manifest = Minimal() with
        {
            Provenance = new PackageProvenance
            {
                Source = "Example",
                License = "MIT",
                ProvenanceClass = provenanceClass,
            },
        };

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.MissingApprovalReference);
    }

    [Theory]
    [InlineData(ProvenanceClass.OpenSourceReference)]
    [InlineData(ProvenanceClass.BehavioralReference)]
    [InlineData(ProvenanceClass.IndependentCapture)]
    [InlineData(ProvenanceClass.OfficialDocumentation)]
    public void Validate_ProvenanceThatOnlyLearnedFacts_NeedsNoApproval(ProvenanceClass provenanceClass)
    {
        // Protocol facts - a data address, a report prefix, a buffer length - are facts about the
        // hardware. Learning them from another implementation and writing our own code is not a
        // licensing event, so it must not require one.
        PluginManifest manifest = Minimal() with
        {
            Provenance = new PackageProvenance
            {
                Source = "Example",
                License = "GPL-3.0-or-later",
                ProvenanceClass = provenanceClass,
            },
        };

        Assert.DoesNotContain(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.MissingApprovalReference);
    }

    [Fact]
    public void Validate_AnInvertedApiRange_IsRejected()
    {
        PluginManifest manifest = Minimal() with { MinApiVersion = 3, MaxApiVersion = 2 };

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            e => e.Code == ManifestValidationCode.InvalidApiRange);
    }

    [Fact]
    public void Validate_APackageWithNoDevices_IsRejected()
    {
        Assert.Contains(
            PluginManifestValidator.Validate(Minimal() with { Devices = [] }),
            e => e.Code == ManifestValidationCode.MissingField);
    }

    [Fact]
    public void Validate_MoreDevicesThanTheLimit_IsRejected()
    {
        DeviceDefinition[] devices = Enumerable
            .Range(0, ManifestLimits.MaxDevices + 1)
            .Select(i => MinimalDevice() with { Id = $"device-{i}" })
            .ToArray();

        Assert.Contains(
            PluginManifestValidator.Validate(Minimal() with { Devices = devices }),
            e => e.Code == ManifestValidationCode.LimitExceeded);
    }

    [Fact]
    public void Validate_ReportsEveryProblemRatherThanStoppingAtTheFirst()
    {
        // A package author fixing a manifest should see the whole list; Device Lab reports them
        // together rather than one per round trip.
        PluginManifest manifest = Minimal() with
        {
            Id = "bad id",
            Version = "nope",
            EntryPoint = "../escape.dll",
        };

        IReadOnlyList<ManifestValidationError> errors = PluginManifestValidator.Validate(manifest);

        Assert.Contains(errors, e => e.Code == ManifestValidationCode.InvalidIdentifier);
        Assert.Contains(errors, e => e.Code == ManifestValidationCode.InvalidVersion);
        Assert.Contains(errors, e => e.Code == ManifestValidationCode.UnsafePath);
    }

    private static IdentityObservation RequiredBoard() => new()
    {
        Signal = IdentitySignal.SmbiosBaseboardProduct,
        Strength = IdentityStrength.Required,
        Values = ["MS-1T52"],
    };

    private static DeviceDefinition MinimalDevice() => new()
    {
        Id = "example-device",
        DisplayName = "Example device",
        Identity = [RequiredBoard()],
    };

    private static PluginManifest Minimal() => new()
    {
        SchemaVersion = 1,
        Id = "wsgm.device.example",
        Version = "1.0.0",
        DisplayName = "Example package",
        Publisher = "Example",
        MinApiVersion = 1,
        MaxApiVersion = 1,
        EntryPoint = "Example.dll",
        Devices = [MinimalDevice()],
        Provenance = new PackageProvenance
        {
            Source = "Example",
            License = "GPL-3.0-or-later",
            ProvenanceClass = ProvenanceClass.IndependentCapture,
        },
    };

    private static PluginManifest WithDevice(DeviceDefinition device) =>
        Minimal() with { Devices = [device] };
}
