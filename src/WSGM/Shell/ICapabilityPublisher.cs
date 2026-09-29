using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>
///     The capability stream of one publisher, as a <see cref="DeviceCapabilityRouter" /> consumes it: the
///     device package's runtime, or a graphics package's channel.
/// </summary>
/// <remarks>
///     The router validates, projects and commands through this and nothing else, so one router type
///     serves both kinds of publisher while each keeps its own lifecycle owner.
/// </remarks>
internal interface ICapabilityPublisher
{
    /// <summary>The current cycle generation. Only the publisher's owner advances it.</summary>
    long CycleGeneration { get; }

    /// <summary>The capability roles the package manifest declares; the router refuses any other.</summary>
    IReadOnlyList<CapabilityRole> DeclaredCapabilities { get; }

    /// <summary>Raised with each descriptor set the publisher accepted from its plugin.</summary>
    event Action<CapabilityDescriptorSet>? DescriptorSetReceived;

    /// <summary>Raised with each state observation, sequenced within the cycle.</summary>
    event Action<CapabilityStateDelta>? CapabilityStateReceived;

    /// <summary>Sends one command to the plugin.</summary>
    /// <param name="command">The validated command.</param>
    /// <param name="cancellationToken">Cancels the caller's wait; the plugin's work may continue.</param>
    /// <returns>The immediate result, and the late completion when the caller stopped waiting first.</returns>
    Task<DeviceCommandDispatch> ExecuteCommandAsync(CapabilityCommand command, CancellationToken cancellationToken);
}
