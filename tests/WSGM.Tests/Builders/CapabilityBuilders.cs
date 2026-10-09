using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests.Builders;

/// <summary>Capability descriptors, states and views for graphics publisher and profile scope tests.</summary>
internal static class CapabilityBuilders
{
    /// <summary>The profile key of the test graphics publisher.</summary>
    internal const string GpuPublisher = "gpu:wsgm.test-gpu";

    /// <summary>The test graphics publisher's instance.</summary>
    internal static readonly PluginInstanceIdentity GpuInstance = new("wsgm.test-gpu", "default");

    internal static CapabilityValue Flag(bool value)
    {
        return new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = value };
    }

    /// <summary>A writable generic toggle with a profile scope.</summary>
    internal static CapabilityDescriptor Toggle(CapabilityProfileScope scope, string id = "graphics.toggle")
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = CapabilityRole.GenericToggle,
            ValueKind = CapabilityValueKind.Boolean,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Toggle" },
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
            ProfileScope = scope
        };
    }

    /// <summary>A variable-refresh descriptor for one display instance.</summary>
    internal static CapabilityDescriptor Vrr(string? instanceId)
    {
        return Toggle(CapabilityProfileScope.Switched, "display.vrr") with
        {
            Role = CapabilityRole.VariableRefreshRate,
            InstanceId = instanceId,
            Display = new CapabilityDisplay { Key = DisplayKey.VariableRefreshRate }
        };
    }

    /// <summary>A graphics publisher's view of one descriptor, observed off.</summary>
    internal static DeviceCapabilityView View(CapabilityDescriptor descriptor, CapabilityValue? desired,
        CapabilityValue? global, ProfileSource source = ProfileSource.None)
    {
        return new DeviceCapabilityView(
            descriptor,
            new CapabilityProjection
            {
                State = State(1, Flag(false)) with
                {
                    CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId
                },
                DesiredValue = desired,
                DesiredSource = desired is null ? ProfileSource.None : source,
                GlobalDesiredValue = global,
                ProfileScope = descriptor.ProfileScope,
                ApplyTiming = descriptor.ApplyTiming
            },
            null) { Publisher = GpuPublisher };
    }

    /// <summary>A fresh verified observation of <c>graphics.toggle</c> in cycle 1.</summary>
    internal static CapabilityState State(long descriptorGeneration, CapabilityValue observed)
    {
        return new CapabilityState
        {
            CapabilityId = "graphics.toggle",
            Available = true,
            Quality = HardwareStateQuality.Verified,
            ObservedValue = observed,
            ObservedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A descriptor set in cycle 1.</summary>
    internal static CapabilityDescriptorSet Set(long generation, params CapabilityDescriptor[] descriptors)
    {
        return new CapabilityDescriptorSet { Descriptors = descriptors };
    }

    /// <summary>A command turning <c>graphics.toggle</c> on.</summary>
    internal static CapabilityCommand Command(long cycleGeneration)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = "graphics.toggle",
            RequestedValue = Flag(true),
            Deadline = Deadline.After(TimeSpan.FromSeconds(5))
        };
    }

    /// <summary>A game profile holding one value for a publisher.</summary>
    internal static GameProfile GameWith(string id, List<string> processes, List<string> learned, bool enabled,
        string publisher, string capabilityId)
    {
        GameProfile game = new() { Id = id, ProcessNames = processes, Executables = learned, Enabled = enabled };
        game.Values.SetDevice(publisher, capabilityId, null, Flag(true));
        return game;
    }
}
