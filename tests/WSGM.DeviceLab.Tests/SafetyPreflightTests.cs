using WSGM.Device.Contracts.Lifecycle;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Tests;

/// <summary>Preflight keeps production ownership and imported evidence outside mutation authority.</summary>
public class SafetyPreflightTests
{
    [Fact]
    public void ProductionOwnedRead_UsesOnlyThePluginDiagnosticSession()
    {
        DeviceLabPreflightDecision decision = DeviceLabSafetyPreflight.Evaluate(
            Requirements(DeviceLabOperationAccess.ReviewedReadProbe),
            Snapshot(DeviceOwnerDiscoveryState.PresentWithDiagnostics, ResourceState.Owned));

        Assert.NotEqual(DeviceLabDoctorStatus.Blocked, decision.Status);
        Assert.Equal(DeviceLabAccessRoute.ProductionDiagnosticSession, decision.Route);
        Assert.Equal(LeaseKind.Diagnostic, decision.RequiredLease);
        Assert.Equal(8, decision.HostGeneration);
        Assert.Equal(13, decision.DeviceGeneration);
        Assert.False(decision.ReceivesRawTransport);
        Assert.False(decision.MayChangeDeviceCycle);
        Assert.False(decision.MayDisableDeviceIntegration);
    }

    [Fact]
    public void ProductionOwnedTrial_RequiresAnOrderlyReleaseBeforeExperimentLease()
    {
        DeviceLabPreflightDecision blocked = DeviceLabSafetyPreflight.Evaluate(
            Requirements(DeviceLabOperationAccess.MutationTrial),
            Snapshot(DeviceOwnerDiscoveryState.PresentWithDiagnostics, ResourceState.Owned));
        DeviceLabPreflightDecision released = DeviceLabSafetyPreflight.Evaluate(
            Requirements(DeviceLabOperationAccess.MutationTrial),
            Snapshot(DeviceOwnerDiscoveryState.PresentWithDiagnostics, ResourceState.Idle));

        Assert.Equal(DeviceLabDoctorStatus.Blocked, blocked.Status);
        Assert.Equal(DeviceLabAccessRoute.None, blocked.Route);
        Assert.Contains(blocked.Checks, check => check.Code == "owner.release-required");
        Assert.Equal(DeviceLabAccessRoute.ExperimentLease, released.Route);
        Assert.Equal(LeaseKind.Experiment, released.RequiredLease);
    }

    [Fact]
    public void UninspectableOrAmbiguousOwner_FailsClosedWithoutChangingTheDeviceCycle()
    {
        foreach (DeviceOwnerDiscoveryState state in new[]
        {
            DeviceOwnerDiscoveryState.PresentWithoutDiagnostics,
            DeviceOwnerDiscoveryState.Ambiguous,
        })
        {
            DeviceLabPreflightDecision decision = DeviceLabSafetyPreflight.Evaluate(
                Requirements(DeviceLabOperationAccess.PassiveObservation),
                Snapshot(state, resourceState: null));

            Assert.Equal(DeviceLabDoctorStatus.Blocked, decision.Status);
            Assert.Equal(DeviceLabAccessRoute.None, decision.Route);
            Assert.False(decision.MayChangeDeviceCycle);
            Assert.False(decision.MayDisableDeviceIntegration);
        }
    }

    [Fact]
    public void MatchingProcessName_IsAWarningNotOwnershipOrAuthorization()
    {
        DeviceLabSafetySnapshot snapshot = Snapshot(DeviceOwnerDiscoveryState.Absent, resourceState: null) with
        {
            ExternalComponents =
            [
                new DeviceLabExternalComponent
                {
                    ComponentId = "vendor-center",
                    Kind = "process",
                    Present = true,
                    Accessible = true,
                    ResourceId = "power",
                    OwnershipEvidence = DeviceLabOwnershipEvidence.NameOnly,
                },
            ],
        };

        DeviceLabPreflightDecision decision = DeviceLabSafetyPreflight.Evaluate(
            Requirements(DeviceLabOperationAccess.ReviewedReadProbe),
            snapshot);

        Assert.Equal(DeviceLabDoctorStatus.Warning, decision.Status);
        Assert.Equal(DeviceLabAccessRoute.DirectReadOnly, decision.Route);
        Assert.Contains(decision.Checks, check => check.Code == "component.name-only");
    }

    [Fact]
    public void DemonstratedExternalOwnership_BlocksOnlyTheNamedResource()
    {
        DeviceLabExternalComponent owner = new()
        {
            ComponentId = "vendor-service",
            Kind = "service",
            Present = true,
            Accessible = true,
            ResourceId = "power",
            OwnershipEvidence = DeviceLabOwnershipEvidence.ExclusiveAccessFailure,
        };
        DeviceLabSafetySnapshot snapshot = Snapshot(DeviceOwnerDiscoveryState.Absent, resourceState: null) with
        {
            ExternalComponents = [owner],
        };

        DeviceLabPreflightDecision power = DeviceLabSafetyPreflight.Evaluate(
            Requirements(DeviceLabOperationAccess.ReviewedReadProbe),
            snapshot);
        DeviceLabPreflightDecision lighting = DeviceLabSafetyPreflight.Evaluate(
            Requirements(DeviceLabOperationAccess.ReviewedReadProbe) with { ResourceId = "lighting" },
            snapshot);

        Assert.Equal(DeviceLabDoctorStatus.Blocked, power.Status);
        Assert.NotEqual(DeviceLabDoctorStatus.Blocked, lighting.Status);
    }

    [Fact]
    public void PowerThermalElevationHelperAndCatalogPrerequisites_AreCheckedBeforeAccess()
    {
        DeviceLabOperationRequirements requirements = Requirements(DeviceLabOperationAccess.MutationTrial) with
        {
            RequiresExternalPower = true,
            MinimumBatteryPercent = 50,
            MaximumTemperatureCelsius = 80,
            RequiresElevation = true,
            RequiredReviewedHelperId = "reviewed-helper",
            CatalogBlockReasons = ["Required provider version is missing."],
        };
        DeviceLabSafetySnapshot snapshot = Snapshot(DeviceOwnerDiscoveryState.Absent, resourceState: null) with
        {
            DeviceIntegrationEnabled = false,
            IsElevated = false,
            PowerThermal = new DeviceLabPowerThermalSnapshot
            {
                ExternalPowerConnected = false,
                BatteryPercent = 20,
                TemperatureCelsius = 91,
            },
        };

        DeviceLabPreflightDecision decision = DeviceLabSafetyPreflight.Evaluate(requirements, snapshot);

        Assert.Equal(DeviceLabDoctorStatus.Blocked, decision.Status);
        Assert.Contains(decision.Checks, check => check.Code == "power.external");
        Assert.Contains(decision.Checks, check => check.Code == "power.battery");
        Assert.Contains(decision.Checks, check => check.Code == "thermal.hot");
        Assert.Contains(decision.Checks, check => check.Code == "permission.elevation");
        Assert.Contains(decision.Checks, check => check.Code == "permission.helper");
        Assert.Contains(decision.Checks, check => check.Code == "catalog.prerequisite");
    }

    public static TheoryData<DeviceLabOperationOrigin> ImportedOrigins => new()
    {
        DeviceLabOperationOrigin.ImportedCapture,
        DeviceLabOperationOrigin.ImportedRecipe,
        DeviceLabOperationOrigin.ImportedManifest,
        DeviceLabOperationOrigin.ImportedPluginPackage,
        DeviceLabOperationOrigin.ImportedEvidenceLock,
        DeviceLabOperationOrigin.ImportedAcceptanceManifest,
    };

    [Theory]
    [MemberData(nameof(ImportedOrigins))]
    public void ImportedArtifact_CanNeverAuthorizeOrSupplyMutation(DeviceLabOperationOrigin origin)
    {
        DeviceLabOperationRequirements requirements = Requirements(DeviceLabOperationAccess.MutationTrial) with
        {
            Origin = origin,
        };
        DeviceLabSafetySnapshot snapshot = Snapshot(DeviceOwnerDiscoveryState.Absent, resourceState: null) with
        {
            DeviceIntegrationEnabled = false,
        };

        DeviceLabPreflightDecision decision = DeviceLabSafetyPreflight.Evaluate(requirements, snapshot);

        Assert.Equal(DeviceLabDoctorStatus.Blocked, decision.Status);
        Assert.Equal(DeviceLabAccessRoute.None, decision.Route);
        Assert.Contains(decision.Checks, check => check.Code == "authority.imported");
    }

    [Fact]
    public void ExternalOrDeveloperReadProbe_RequiresExplicitDeveloperModeAction()
    {
        DeviceLabOperationRequirements requirements = Requirements(DeviceLabOperationAccess.ReviewedReadProbe) with
        {
            Origin = DeviceLabOperationOrigin.SideloadedPackage,
            DeveloperModeApproved = false,
        };

        DeviceLabPreflightDecision decision = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            Snapshot(DeviceOwnerDiscoveryState.Absent, resourceState: null));

        Assert.Equal(DeviceLabDoctorStatus.Blocked, decision.Status);
        Assert.Contains(decision.Checks, check => check.Code == "authority.developer-mode");
    }

    [Fact]
    public void OwnerInspector_ClassifiesOnlyACompleteVersionedSnapshotAsInspectable()
    {
        DeviceLabOwnerInspection absent = DeviceLabOwnerInspector.Classify(
            ownerPresent: false,
            diagnosticsSource: null);
        DeviceLabOwnerInspection uninspectable = DeviceLabOwnerInspector.Classify(
            ownerPresent: true,
            diagnosticsSource: null);
        DeviceLabOwnerInspection inspectable = DeviceLabOwnerInspector.Classify(
            ownerPresent: true,
            new FakeDiagnosticsSource(Snapshot(
                DeviceOwnerDiscoveryState.PresentWithDiagnostics,
                ResourceState.Owned).ActiveDevice));

        Assert.Equal(DeviceOwnerDiscoveryState.Absent, absent.State);
        Assert.Null(absent.DeviceIntegrationEnabled);
        Assert.Equal(DeviceOwnerDiscoveryState.PresentWithoutDiagnostics, uninspectable.State);
        Assert.Equal(DeviceOwnerDiscoveryState.PresentWithDiagnostics, inspectable.State);
        Assert.True(inspectable.DeviceIntegrationEnabled);
        Assert.Equal(8, inspectable.Snapshot!.HostGeneration);
    }

    [Fact]
    public void DiagnosticSession_IsBoundedGenerationPinnedAndSemanticOnly()
    {
        DeviceLabPreflightDecision preflight = DeviceLabSafetyPreflight.Evaluate(
            Requirements(DeviceLabOperationAccess.ReviewedReadProbe),
            Snapshot(DeviceOwnerDiscoveryState.PresentWithDiagnostics, ResourceState.Owned));
        DeviceLabDiagnosticSessionRequest request = new()
        {
            ResourceId = "power",
            HostGeneration = 8,
            DeviceGeneration = 13,
            MaximumObservations = 10,
            Deadline = DateTimeOffset.UnixEpoch.AddSeconds(10),
        };

        DeviceLabDiagnosticSessionAuthorization authorized =
            DeviceLabDiagnosticSessionPolicy.Authorize(
                request,
                preflight,
                currentHostGeneration: 8,
                currentDeviceGeneration: 13,
                DateTimeOffset.UnixEpoch);
        DeviceLabDiagnosticSessionAuthorization stale =
            DeviceLabDiagnosticSessionPolicy.Authorize(
                request,
                preflight,
                currentHostGeneration: 9,
                currentDeviceGeneration: 13,
                DateTimeOffset.UnixEpoch);

        Assert.Equal(DeviceLabDiagnosticAuthorizationStatus.Authorized, authorized.Status);
        Assert.Equal(DeviceLabDiagnosticAuthorizationStatus.StaleGeneration, stale.Status);
        Assert.Equal(LeaseKind.Diagnostic, request.LeaseKind);
        Assert.False(request.MayChangeDeviceCycle);
        Assert.False(request.ReceivesRawTransport);
    }

    private static DeviceLabOperationRequirements Requirements(DeviceLabOperationAccess access) => new()
    {
        OperationId = "power.status",
        ResourceId = "power",
        Access = access,
        Origin = DeviceLabOperationOrigin.ReviewedBuiltInCatalog,
        IsLocallyInstalled = true,
        IsHashPinned = true,
        ExactFamilyMatched = true,
        ExactEndpointMatched = true,
        DeveloperModeApproved = true,
    };

    private static DeviceLabSafetySnapshot Snapshot(
        DeviceOwnerDiscoveryState owner,
        ResourceState? resourceState)
    {
        DeviceDiagnosticsSnapshot? diagnostics = owner is DeviceOwnerDiscoveryState.PresentWithDiagnostics
            ? new DeviceDiagnosticsSnapshot
            {
                SchemaVersion = 1,
                PackageId = "wsgm.device.reference",
                DeviceId = "reference",
                TrustTier = "BuiltIn",
                CycleState = DeviceCycleState.Active,
                HostGeneration = 8,
                DeviceGeneration = 13,
                Resources = resourceState is { } state
                    ? new Dictionary<string, ResourceState> { ["power"] = state }
                    : new Dictionary<string, ResourceState>(),
                CapturedAt = DateTimeOffset.UnixEpoch,
            }
            : null;

        return new DeviceLabSafetySnapshot
        {
            Doctor = PassingDoctor(),
            DeviceIntegrationEnabled = owner is not DeviceOwnerDiscoveryState.Absent,
            OwnerDiscovery = owner,
            ActiveDevice = diagnostics,
            PowerThermal = new DeviceLabPowerThermalSnapshot
            {
                ExternalPowerConnected = true,
                BatteryPercent = 100,
                TemperatureCelsius = 40,
            },
            IsElevated = true,
            IsUserInteractive = true,
            IsContinuousIntegration = false,
        };
    }

    private static DeviceLabDoctorReport PassingDoctor() => new()
    {
        SchemaVersion = DeviceLabDoctor.CurrentSchemaVersion,
        CapturedAt = DateTimeOffset.UnixEpoch,
        Status = DeviceLabDoctorStatus.Pass,
        OutputDirectory = "safe-output",
    };

    private sealed class FakeDiagnosticsSource(DeviceDiagnosticsSnapshot? snapshot)
        : IDeviceOwnerDiagnosticsSource
    {
        public bool TryRead(out DeviceDiagnosticsSnapshot? value)
        {
            value = snapshot;
            return value is not null;
        }
    }
}
