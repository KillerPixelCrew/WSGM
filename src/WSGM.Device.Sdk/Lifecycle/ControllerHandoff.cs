using System.Text.Json.Serialization;

namespace WSGM.Device.Sdk.Lifecycle;

/// <summary>
/// The ordered steps of releasing the physical controller back to its firmware or an external owner.
/// </summary>
/// <remarks>
/// Two-phase, and the order is the whole point. WSGM neutralizes its virtual target but keeps the
/// physical device hidden while the plugin quiesces; only after the plugin confirms it has stopped
/// reading and restored the original mode does WSGM remove the virtual target and its HidHide
/// entries.
/// <para>
/// Doing it the other way — un-hiding first — exposes a device the plugin is still holding, so for
/// the length of the handoff Steam and any running game would see both the physical controller and
/// the virtual target at once. That is the duplicate-input state the single-target rule exists to
/// prevent, and it happens exactly when the user is watching.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerHandoffStep>))]
public enum ControllerHandoffStep
{
    /// <summary>Nothing has started.</summary>
    NotStarted,

    /// <summary>WSGM sent a neutral state to the virtual target and stopped forwarding input.</summary>
    VirtualTargetNeutralized,

    /// <summary>The plugin stopped reading the physical controller and closed its handles.</summary>
    PhysicalAcquisitionStopped,

    /// <summary>The plugin restored the controller mode captured at activation.</summary>
    OriginalModeRestored,

    /// <summary>
    /// The expected re-enumeration was observed at the same physical location.
    /// </summary>
    /// <remarks>
    /// Verified by location path rather than by identity: a mode change alters the product ID, and
    /// on the reference hardware the container ID is the null GUID while the USB serial exists in
    /// only one of the two modes.
    /// </remarks>
    TopologyVerified,

    /// <summary>
    /// Re-enumeration could not be confirmed within the budget.
    /// </summary>
    /// <remarks>
    /// A terminal step, not a failure to report as success. The user's stop request is still honoured
    /// and cleanup continues, but the result is recorded as an unverified handoff.
    /// </remarks>
    TopologyUnverified,

    /// <summary>WSGM removed the virtual target and only its own HidHide entries.</summary>
    WsgmStateRemoved,
}

/// <summary>
/// How a completed controller handoff turned out.
/// </summary>
/// <remarks>
/// Separate from the step so a timeout is never presented as a clean release. WSGM cannot promise
/// that an external manager will find the device in the state it expects unless the whole sequence
/// was observed.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerHandoffResult>))]
public enum ControllerHandoffResult
{
    /// <summary>Still in progress.</summary>
    InProgress,

    /// <summary>
    /// Every step completed and was observed. The device is released and available to another owner.
    /// </summary>
    /// <remarks>
    /// This is a claim about WSGM's own state only: acquisition released, original mode restored,
    /// re-enumeration seen at the same location, WSGM's HidHide entries removed. It is never a claim
    /// that Handheld Companion or any other manager has actually taken the device — WSGM does not
    /// wait for that, ask about it, or probe another process's ownership.
    /// </remarks>
    ReleasedVerified,

    /// <summary>
    /// Cleanup finished, but at least one step could not be confirmed. Journalled for the next start.
    /// </summary>
    ReleasedUnverified,
}

/// <summary>
/// Whether the device cycle continues after a controller handoff.
/// </summary>
public enum HandoffScope
{
    /// <summary>
    /// Only WSGM controller management was turned off.
    /// </summary>
    /// <remarks>
    /// The host, every non-controller resource, the OEM event path, and the firmware-chord suppressor
    /// all continue. Turning off controller emulation is not a reason to stop managing fans.
    /// </remarks>
    ControllerOnly,

    /// <summary>
    /// The whole device cycle is ending, because WSGM is exiting or Device Integration was turned off.
    /// </summary>
    FullDeactivation,
}
