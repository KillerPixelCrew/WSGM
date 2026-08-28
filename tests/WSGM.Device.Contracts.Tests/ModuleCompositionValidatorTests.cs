using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Modules;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of module composition: what a device may reuse, and the one thing
/// reuse must never carry across a board boundary.
/// </summary>
public class ModuleCompositionValidatorTests
{
    [Fact]
    public void Validate_AWellFormedComposition_HasNoErrors()
    {
        Assert.Empty(ModuleCompositionValidator.Validate(A2VmDevice(), Catalog()));
    }

    [Fact]
    public void Validate_APolicyModuleVerifiedOnAnotherBoard_IsRejected()
    {
        // The A1M power policy carries larger limits. Composing it here would apply another board's
        // ceilings to this one - the concrete failure the whole layer split exists to prevent.
        DeviceDefinition device = A2VmDevice() with
        {
            Modules =
            [
                new ModuleReference { Id = "MsiWmiPlatform", Version = 1, Layer = ModuleLayer.Transport },
                new ModuleReference { Id = "MsiClawA1MPowerPolicy", Version = 1, Layer = ModuleLayer.Policy },
            ],
            Capabilities = ["power.primary-limit"],
        };

        IReadOnlyList<ModuleCompositionError> errors =
            ModuleCompositionValidator.Validate(device, Catalog());

        Assert.Contains(errors, e => e.Code == ModuleCompositionCode.ModuleNotVerifiedForDevice);
    }

    [Fact]
    public void Validate_ATransportThatClaimsDeviceScope_IsRejected()
    {
        // A transport that names devices has stopped being a transport. Whatever made it
        // model-specific is a constant that belongs where the scope rule can reach it.
        Dictionary<string, ImplementationModule> catalog = Catalog();
        catalog["MsiWmiPlatform"] = catalog["MsiWmiPlatform"] with { VerifiedDeviceIds = ["ms-1t52"] };

        IReadOnlyList<ModuleCompositionError> errors =
            ModuleCompositionValidator.Validate(A2VmDevice(), catalog);

        Assert.Contains(errors, e => e.Code == ModuleCompositionCode.ReusableModuleDeclaresDeviceScope);
    }

    [Fact]
    public void Validate_APolicyModuleWithNoDeviceScope_IsRejected()
    {
        Dictionary<string, ImplementationModule> catalog = Catalog();
        catalog["MsiClawA2VmPowerPolicy"] =
            catalog["MsiClawA2VmPowerPolicy"] with { VerifiedDeviceIds = [] };

        IReadOnlyList<ModuleCompositionError> errors =
            ModuleCompositionValidator.Validate(A2VmDevice(), catalog);

        Assert.Contains(errors, e => e.Code == ModuleCompositionCode.DeviceSpecificModuleMissingScope);
    }

    [Fact]
    public void Validate_AModuleMissingFromTheCatalog_IsRejected()
    {
        DeviceDefinition device = A2VmDevice() with
        {
            Modules = [new ModuleReference { Id = "NotInCatalog", Version = 1, Layer = ModuleLayer.Transport }],
            Capabilities = [],
        };

        Assert.Contains(
            ModuleCompositionValidator.Validate(device, Catalog()),
            e => e.Code == ModuleCompositionCode.UnknownModule);
    }

    [Fact]
    public void Validate_APinnedVersionThatDoesNotExist_IsRejected()
    {
        // Composition pins an exact version so a catalog update cannot silently change the layout,
        // limits, or recovery policy a device runs with.
        DeviceDefinition device = A2VmDevice() with
        {
            Modules =
            [
                new ModuleReference { Id = "MsiWmiPlatform", Version = 99, Layer = ModuleLayer.Transport },
            ],
            Capabilities = [],
        };

        Assert.Contains(
            ModuleCompositionValidator.Validate(device, Catalog()),
            e => e.Code == ModuleCompositionCode.UnknownModule);
    }

    [Fact]
    public void Validate_AnAbsentDependency_IsRejected()
    {
        DeviceDefinition device = A2VmDevice() with
        {
            Modules =
            [
                new ModuleReference { Id = "MsiClawA2VmPowerPolicy", Version = 1, Layer = ModuleLayer.Policy },
            ],
            Capabilities = ["power.primary-limit"],
        };

        Assert.Contains(
            ModuleCompositionValidator.Validate(device, Catalog()),
            e => e.Code == ModuleCompositionCode.MissingDependency);
    }

    [Fact]
    public void Validate_ADependencyOutsideItsAcceptedVersionRange_IsRejected()
    {
        Dictionary<string, ImplementationModule> catalog = Catalog();
        catalog["MsiWmiPlatform"] = catalog["MsiWmiPlatform"] with { Version = 5 };

        DeviceDefinition device = A2VmDevice() with
        {
            Modules =
            [
                new ModuleReference { Id = "MsiWmiPlatform", Version = 5, Layer = ModuleLayer.Transport },
                new ModuleReference { Id = "MsiClawA2VmPowerPolicy", Version = 1, Layer = ModuleLayer.Policy },
            ],
            Capabilities = ["power.primary-limit"],
        };

        Assert.Contains(
            ModuleCompositionValidator.Validate(device, catalog),
            e => e.Code == ModuleCompositionCode.DependencyVersionOutOfRange);
    }

    [Fact]
    public void Validate_TwoModulesThatDeclareEachOtherIncompatible_AreRejected()
    {
        Dictionary<string, ImplementationModule> catalog = Catalog();
        catalog["MsiClawMcu"] = catalog["MsiClawMcu"] with { Conflicts = ["MsiWmiPlatform"] };

        DeviceDefinition device = A2VmDevice() with
        {
            Modules =
            [
                new ModuleReference { Id = "MsiWmiPlatform", Version = 1, Layer = ModuleLayer.Transport },
                new ModuleReference { Id = "MsiClawMcu", Version = 2, Layer = ModuleLayer.Protocol },
            ],
            Capabilities = [],
        };

        Assert.Contains(
            ModuleCompositionValidator.Validate(device, catalog),
            e => e.Code == ModuleCompositionCode.ConflictingModules);
    }

    [Theory]
    [InlineData(PersistenceClass.DevicePersistent)]
    [InlineData(PersistenceClass.Unknown)]
    public void Validate_AWriteThatMayPersistWithoutASnapshot_IsRejected(PersistenceClass persistence)
    {
        // Unknown is treated exactly like DevicePersistent. Assuming a setter is volatile because it
        // looks like one is how a probe leaves a device changed with nothing to restore from.
        Dictionary<string, ImplementationModule> catalog = Catalog();
        catalog["MsiClawA2VmPowerPolicy"] = catalog["MsiClawA2VmPowerPolicy"] with
        {
            Safety = new ModuleSafety { Writes = true, Persistence = persistence },
            Recovery = new ModuleRecovery { SnapshotRequired = false },
        };

        Assert.Contains(
            ModuleCompositionValidator.Validate(A2VmDevice(), catalog),
            e => e.Code == ModuleCompositionCode.PersistentWriteWithoutSnapshot);
    }

    [Fact]
    public void Validate_AVolatileWriteWithoutASnapshot_IsAllowed()
    {
        Dictionary<string, ImplementationModule> catalog = Catalog();
        catalog["MsiClawA2VmPowerPolicy"] = catalog["MsiClawA2VmPowerPolicy"] with
        {
            Safety = new ModuleSafety { Writes = true, Persistence = PersistenceClass.Volatile },
            Recovery = new ModuleRecovery { SnapshotRequired = false },
        };

        Assert.DoesNotContain(
            ModuleCompositionValidator.Validate(A2VmDevice(), catalog),
            e => e.Code == ModuleCompositionCode.PersistentWriteWithoutSnapshot);
    }

    [Fact]
    public void Validate_ACapabilityNoComposedModuleImplements_IsRejected()
    {
        // Otherwise the overlay publishes a control whose command can only ever time out.
        DeviceDefinition device = A2VmDevice() with
        {
            Capabilities = ["power.primary-limit", "lighting.zones"],
        };

        Assert.Contains(
            ModuleCompositionValidator.Validate(device, Catalog()),
            e => e.Code == ModuleCompositionCode.CapabilityWithoutImplementation);
    }

    private static DeviceDefinition A2VmDevice() => new()
    {
        Id = "ms-1t52",
        DisplayName = "MSI Claw 8 AI+ A2VM",
        Identity =
        [
            new IdentityObservation
            {
                Signal = IdentitySignal.SmbiosBaseboardProduct,
                Strength = IdentityStrength.Required,
                Values = ["MS-1T52"],
            },
        ],
        Modules =
        [
            new ModuleReference { Id = "MsiWmiPlatform", Version = 1, Layer = ModuleLayer.Transport },
            new ModuleReference { Id = "MsiClawA2VmPowerPolicy", Version = 1, Layer = ModuleLayer.Policy },
        ],
        Capabilities = ["power.primary-limit"],
    };

    private static Dictionary<string, ImplementationModule> Catalog() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MsiWmiPlatform"] = new ImplementationModule
            {
                Id = "MsiWmiPlatform",
                Version = 1,
                Layer = ModuleLayer.Transport,
                DisplayName = "MSI named-method WMI transport",
                Safety = new ModuleSafety { Writes = true, Persistence = PersistenceClass.Volatile },
                Recovery = new ModuleRecovery(),
                Provenance = Provenance(),
            },
            ["MsiClawMcu"] = new ImplementationModule
            {
                Id = "MsiClawMcu",
                Version = 2,
                Layer = ModuleLayer.Protocol,
                DisplayName = "MSI Claw 64-byte MCU protocol",
                Safety = new ModuleSafety { Writes = true, Persistence = PersistenceClass.Volatile },
                Recovery = new ModuleRecovery(),
                Provenance = Provenance(),
            },
            ["MsiClawA2VmPowerPolicy"] = new ImplementationModule
            {
                Id = "MsiClawA2VmPowerPolicy",
                Version = 1,
                Layer = ModuleLayer.Policy,
                DisplayName = "MS-1T52 power policy",
                VerifiedDeviceIds = ["ms-1t52"],
                Capabilities = ["power.primary-limit"],
                Dependencies = [new ModuleDependency("MsiWmiPlatform", 1, 2)],
                Safety = new ModuleSafety { Writes = true, Persistence = PersistenceClass.Volatile },
                Recovery = new ModuleRecovery { SnapshotRequired = true, RollbackVerifiedOnHardware = true },
                Provenance = Provenance(),
            },
            ["MsiClawA1MPowerPolicy"] = new ImplementationModule
            {
                Id = "MsiClawA1MPowerPolicy",
                Version = 1,
                Layer = ModuleLayer.Policy,
                DisplayName = "MS-1T41 power policy",
                VerifiedDeviceIds = ["ms-1t41"],
                Capabilities = ["power.primary-limit"],
                Safety = new ModuleSafety { Writes = true, Persistence = PersistenceClass.Volatile },
                Recovery = new ModuleRecovery { SnapshotRequired = true },
                Provenance = Provenance(),
            },
        };

    private static PackageProvenance Provenance() => new()
    {
        Source = "WSGM first-party",
        License = "GPL-3.0-or-later",
        ProvenanceClass = ProvenanceClass.IndependentCapture,
    };
}
