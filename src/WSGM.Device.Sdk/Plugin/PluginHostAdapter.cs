using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Device.Sdk.Plugin;

/// <summary>
/// Semantic publication surface WSGM gives to exactly one active plugin.
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
    /// <returns>A task completing after WSGM accepted it.</returns>
    ValueTask PublishDescriptorsAsync(
        CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken);

    /// <summary>Publishes one capability observation.</summary>
    /// <param name="state">Live semantic state.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after WSGM accepted it.</returns>
    ValueTask PublishCapabilityStateAsync(
        CapabilityState state,
        CancellationToken cancellationToken);

    /// <summary>Publishes exact physical identities WSGM may use for its HidHide transaction.</summary>
    /// <param name="devices">Plugin-owned physical interfaces.</param>
    /// <param name="output">What the controller can do with haptic output, or null for none.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after WSGM accepted them.</returns>
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
    /// <returns>A task completing after WSGM accepted them.</returns>
    ValueTask PublishOemControlsAsync(
        IReadOnlyList<OemControlDescriptor> controls,
        CancellationToken cancellationToken);

    /// <summary>Publishes one deduplicated OEM-control event.</summary>
    /// <param name="controlEvent">Logical event.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after WSGM accepted it.</returns>
    ValueTask PublishOemEventAsync(
        OemControlEvent controlEvent,
        CancellationToken cancellationToken);

    /// <summary>Declares the settings WSGM should draw, validate, store, and localize.</summary>
    /// <param name="manifest">Typed elements and the sections they belong to.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after WSGM accepted it.</returns>
    /// <remarks>
    /// A declaration, never UI. WSGM refuses a manifest that does not validate and keeps the
    /// previous one, so a plugin cannot half-draw a page by publishing a broken replacement.
    /// <para>
    /// Settings are preferences WSGM stores and hands back; anything that writes hardware when the
    /// user moves it is a capability and belongs in the descriptor set instead.
    /// </para>
    /// </remarks>
    ValueTask PublishSettingsManifestAsync(
        PluginSettingsManifest manifest,
        CancellationToken cancellationToken);

    /// <summary>Writes one diagnostic line into WSGM's log.</summary>
    /// <param name="level">How much the line matters.</param>
    /// <param name="scope">Subsystem producing it, used as the log prefix.</param>
    /// <param name="message">The line; truncated past <see cref="PluginTrace.MaxMessageLength"/>.</param>
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

    /// <summary>Records a polled state, writing only when that key's value changed.</summary>
    /// <param name="level">Level for the line when it is written.</param>
    /// <param name="scope">Subsystem producing the line.</param>
    /// <param name="key">Stable identity of the thing observed, unique within <paramref name="scope"/>.</param>
    /// <param name="message">The current state.</param>
    /// <remarks>
    /// Hosts should suppress an unchanged repeat and count it, so the next line that does change can
    /// report how long the previous state held. The default implementation writes every call, which
    /// keeps a host built against an earlier version of this interface correct — repetitive, but
    /// never missing a line.
    /// </remarks>
    void TraceChange(DeviceTraceLevel level, string scope, string key, string message) =>
        Trace(level, scope, message);

    /// <summary>Reports a background service failure that invalidates the active device cycle.</summary>
    /// <param name="scope">Subsystem that faulted.</param>
    /// <param name="message">Bounded diagnostic detail.</param>
    /// <remarks>
    /// Use this only when work started by the plugin fails after its initiating lifecycle call has
    /// already returned. Synchronous lifecycle and command failures continue to travel through
    /// their normal result or exception path.
    /// </remarks>
    void ReportFault(string scope, string message) =>
        Trace(DeviceTraceLevel.Error, scope, message);
}
