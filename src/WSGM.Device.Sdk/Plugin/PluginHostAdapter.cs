using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Ipc;

namespace WSGM.Device.Sdk.Plugin;

/// <summary>
/// Semantic publication surface DeviceHost gives to exactly one active plugin.
/// </summary>
/// <remarks>
/// No method carries a raw transport, arbitrary operation, path, script, or executable. The plugin
/// owns its implementation and publishes only the device-independent facts WSGM consumes.
/// </remarks>
public interface IPluginHostAdapter
{
    /// <summary>Current process/reconnect cycle generation.</summary>
    long CycleGeneration { get; }

    /// <summary>Publishes an immutable replacement descriptor set.</summary>
    /// <param name="descriptors">Complete descriptor set.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after DeviceHost accepted it.</returns>
    ValueTask PublishDescriptorsAsync(
        CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken);

    /// <summary>Publishes one capability observation.</summary>
    /// <param name="state">Live semantic state.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after DeviceHost accepted it.</returns>
    ValueTask PublishCapabilityStateAsync(
        CapabilityState state,
        CancellationToken cancellationToken);

    /// <summary>Publishes exact physical identities WSGM may use for its HidHide transaction.</summary>
    /// <param name="devices">Plugin-owned physical interfaces.</param>
    /// <param name="output">What the controller can do with haptic output, or null for none.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after DeviceHost accepted them.</returns>
    ValueTask PublishPhysicalDevicesAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        HapticCapabilities? output,
        CancellationToken cancellationToken);

    /// <summary>Publishes one complete canonical controller sample.</summary>
    /// <param name="sample">Normalized physical state.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing when the bounded state channel accepted it.</returns>
    ValueTask PublishControllerSampleAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken);

    /// <summary>Publishes the closed set of assignable OEM controls.</summary>
    /// <param name="controls">Logical device controls.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after DeviceHost accepted them.</returns>
    ValueTask PublishOemControlsAsync(
        IReadOnlyList<OemControlDescriptor> controls,
        CancellationToken cancellationToken);

    /// <summary>Publishes one deduplicated OEM-control event.</summary>
    /// <param name="controlEvent">Logical event.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after DeviceHost accepted it.</returns>
    ValueTask PublishOemEventAsync(
        OemControlEvent controlEvent,
        CancellationToken cancellationToken);

    /// <summary>Writes one diagnostic line into WSGM's log.</summary>
    /// <param name="level">How much the line matters.</param>
    /// <param name="scope">Subsystem producing it, used as the log prefix.</param>
    /// <param name="message">The line; truncated past <see cref="DeviceTraceMessage.MaxMessageLength"/>.</param>
    /// <remarks>
    /// Deliberately synchronous, void, and documented never to throw, unlike every publication on
    /// this interface. That is the whole point: a plugin author instruments a decision only if
    /// doing so costs one statement, and an <c>await</c> that can fail inside an <c>if</c> branch
    /// or a <c>catch</c> is not one statement. The Claw plugin shipped 5,972 lines with no
    /// diagnostics at all against the async-only surface this replaces.
    /// <para>
    /// Delivery is best-effort and unordered with respect to publications. Never make a control
    /// decision depend on a trace, and never trace inside the controller sample loop — it runs at
    /// ~125 Hz and would out-write everything else in the log.
    /// </para>
    /// </remarks>
    void Trace(DeviceTraceLevel level, string scope, string message);
}
