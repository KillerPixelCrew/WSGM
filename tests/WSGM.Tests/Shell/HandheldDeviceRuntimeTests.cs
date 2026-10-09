using LibHandheld;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;
using WSGM.Testing;
using P = LibHandheld.Contracts;

namespace WSGM.Tests.Shell;

public sealed class HandheldDeviceRuntimeTests
{
    private static P.DeviceIdentitySnapshot ClawIdentity()
    {
        return new P.DeviceIdentitySnapshot
        {
            BaseboardManufacturer = "MICRO-STAR INTERNATIONAL CO., LTD.", BaseboardProduct = "MS-1T52"
        };
    }

    [Fact]
    public void DirectDetectionUsesThePlainFamilyIdentity()
    {
        var definition = HandheldDevice.Detect(ClawIdentity());
        Assert.NotNull(definition);
        Assert.Equal("ms-1t52", definition.Id);
        Assert.Equal("msi-claw", definition.FamilyId);
    }

    [Fact]
    public void UnsupportedDesktopHasNoNativeDefinition()
    {
        Assert.Null(HandheldDevice.Detect(new P.DeviceIdentitySnapshot
            { BaseboardManufacturer = "Desktop manufacturer", BaseboardProduct = "MS-1T52" }));
    }

    [Fact]
    public async Task CreationUsesTheSuppliedIdentityAndOneFamilyDirectory()
    {
        using TemporaryDirectory temporary = new();
        var identity = ClawIdentity();
        var definition = HandheldDevice.Detect(identity)!;
        await using var runtime = await HandheldDeviceRuntime.CreateAsync(definition, identity,
            CancellationToken.None, temporary.Root);
        Assert.Equal(Path.Combine(temporary.Root, definition.FamilyId), runtime.StateDirectory);
        Assert.Equal(definition.DeclaredRoles.Select(role => (CapabilityRole)role), runtime.DeclaredCapabilities);
        Assert.False(runtime.IsActive);
    }

    [Fact]
    public async Task CancelledConstructionDoesNotAcquireNativeServices()
    {
        var identity = ClawIdentity();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HandheldDeviceRuntime.CreateAsync(
            HandheldDevice.Detect(identity)!, identity, canceled.Token, Path.GetTempPath()));
    }

    [Fact]
    public async Task CommandBeforeStartIsRejectedAndDisposeOnlyReleasesHandles()
    {
        using TemporaryDirectory temporary = new();
        var identity = ClawIdentity();
        var runtime = await HandheldDeviceRuntime.CreateAsync(HandheldDevice.Detect(identity)!, identity,
            CancellationToken.None, temporary.Root);
        var dispatch = await runtime.ExecuteCommandAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "power.sustained-limit",
            RequestedValue = CapabilityValue.Integer(15), Deadline = Deadline.After(TimeSpan.FromSeconds(1))
        }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, dispatch.Immediate.Outcome);
        Assert.Null(dispatch.LateCompletion);
        await runtime.DisposeAsync();
        Assert.Equal(DeviceRuntimeExitReason.Intentional, (await runtime.Completion).Reason);
        Assert.False(File.Exists(Path.Combine(runtime.StateDirectory, "recovery.json")));
    }

    [Fact]
    public void ProjectionPreservesTypedLabelsAndPutsWritableVendorControlsOnTheirOwnPage()
    {
        var descriptors = HandheldUiProjection.Descriptors(new P.CapabilityDescriptorSet
        {
            Descriptors =
            [
                new P.CapabilityDescriptor
                {
                    CapabilityId = "vendor.module", Role = P.CapabilityRole.GenericToggle,
                    ValueKind = P.CapabilityValueKind.Boolean, SupportsWrite = true,
                    Persistence = P.CapabilityPersistence.Volatile,
                    Display = new P.CapabilityDisplay { Key = P.DisplayKey.Custom, CustomText = "Module" }
                },
                new P.CapabilityDescriptor
                {
                    CapabilityId = "scenario", Role = P.CapabilityRole.ScenarioMode,
                    ValueKind = P.CapabilityValueKind.Choice, SupportsWrite = true,
                    Persistence = P.CapabilityPersistence.Volatile,
                    Display = new P.CapabilityDisplay { Key = P.DisplayKey.PerformanceProfile }
                }
            ]
        });
        Assert.Equal("device-controls", descriptors.Descriptors[0].SectionId);
        Assert.Equal("Module", descriptors.Descriptors[0].Display.CustomLabel);
        Assert.Equal("performance", descriptors.Descriptors[1].CategoryId);
        Assert.Equal(CapabilityProminence.Primary, descriptors.Descriptors[1].Prominence);
    }
}
