using LibHandheld;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.Tests.Fakes;
using P = LibHandheld.Contracts;

namespace WSGM.Tests.Shell;

public sealed class HandheldDeviceRuntimeTests
{
    [Theory]
    [InlineData(P.CapabilityRole.LightingBrightness, null)]
    [InlineData(P.CapabilityRole.LightingBrightness, "logo")]
    [InlineData(P.CapabilityRole.LightingEffect, null)]
    [InlineData(P.CapabilityRole.LightingEffect, "logo")]
    [InlineData(P.CapabilityRole.LightingEffectSpeed, null)]
    [InlineData(P.CapabilityRole.LightingEffectSpeed, "logo")]
    public async Task SharedAndNamedLightingControlsKeepTheirExactIdentity(P.CapabilityRole role, string? instance)
    {
        using TemporaryDirectory temporary = new();
        var choice = role == P.CapabilityRole.LightingEffect;
        P.CapabilityDescriptor Descriptor(string? zone) => new()
        {
            CapabilityId = "fixture.lighting", InstanceId = zone, Role = role,
            ValueKind = choice ? P.CapabilityValueKind.Choice : P.CapabilityValueKind.Integer,
            Minimum = choice ? null : 0, Maximum = choice ? null : 100, Step = choice ? null : 1,
            Choices = choice
                ? [new P.CapabilityChoice("solid", new P.CapabilityDisplay { Key = P.DisplayKey.Custom, CustomText = "Solid" })]
                : [],
            SupportsWrite = true, Persistence = P.CapabilityPersistence.Volatile,
            Display = new P.CapabilityDisplay { Key = P.DisplayKey.Custom, CustomText = "Lighting" }
        };
        var engine = new NativeRuntimeEngine([], (_, _) => Task.CompletedTask)
        {
            Publication = new P.CapabilityDescriptorSet { Descriptors = [Descriptor(null), Descriptor("logo")] }
        };
        var definition = new P.HandheldDefinition("fixture-device", "fixture", "Fixture", "fixture");
        var device = new HandheldDevice(definition, new P.DeviceIdentitySnapshot(), temporary.Root, null, engine);
        await using var runtime = new HandheldDeviceRuntime(device);
        await runtime.StartAsync(controllerManagementEnabled: false, CancellationToken.None);

        var dispatch = await runtime.ExecuteCommandAsync(new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "fixture.lighting", InstanceId = instance,
            RequestedValue = choice ? CapabilityValue.Choice("solid") : CapabilityValue.Integer(40),
            Deadline = Deadline.After(TimeSpan.FromSeconds(5))
        }, CancellationToken.None);

        Assert.Equal(CommandOutcome.Applied, dispatch.Immediate.Outcome);
        var command = Assert.Single(engine.Commands);
        Assert.Equal("fixture.lighting", command.CapabilityId);
        Assert.Equal(instance, command.InstanceId);
    }

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
    public async Task CreationDeclaresTheDefinitionRolesAndStartsInactive()
    {
        using TemporaryDirectory temporary = new();
        var identity = ClawIdentity();
        var definition = HandheldDevice.Detect(identity)!;
        await using var runtime = await HandheldDeviceRuntime.CreateAsync(definition, identity,
            CancellationToken.None, temporary.Root);
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
        var definition = HandheldDevice.Detect(identity)!;
        var runtime = await HandheldDeviceRuntime.CreateAsync(definition, identity,
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
        Assert.False(File.Exists(Path.Combine(temporary.Root, definition.FamilyId, "recovery.json")));
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
