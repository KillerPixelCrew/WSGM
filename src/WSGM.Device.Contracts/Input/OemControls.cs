using System;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Capabilities;

namespace WSGM.Device.Contracts.Input;

/// <summary>
/// A logical OEM control published by the plugin.
/// </summary>
/// <remarks>
/// A separate channel from the gamepad, deliberately. OEM controls are the one class of input WSGM
/// permits reassigning, and keeping them out of the canonical gamepad state is what stops that
/// exception from becoming a general remapper: face buttons, sticks, triggers, and the D-pad are not
/// expressible here at all.
/// </remarks>
public sealed record OemControlDescriptor
{
    /// <summary>Stable identifier within the device definition, for example <c>oem1</c>.</summary>
    public required string ControlId { get; init; }

    /// <summary>How WSGM labels it.</summary>
    public required CapabilityDisplay Display { get; init; }

    /// <summary>Where the control physically is, which decides what may be bound to it.</summary>
    public required OemControlPlacement Placement { get; init; }

    /// <summary>Whether the source distinguishes a short press from a long one.</summary>
    public bool SupportsLongPress { get; init; }

    /// <summary>
    /// Whether this control disappears when WSGM controller management is turned off.
    /// </summary>
    /// <remarks>
    /// Declared by the plugin rather than inferred: only it knows whether a control rides on the
    /// physical-controller resource. On the reference handheld the rear paddles do — they are visible
    /// only in the acquisition mode the plugin selects — while the front buttons arrive over a
    /// separate vendor event channel and survive.
    /// </remarks>
    public bool RequiresControllerAcquisition { get; init; }
}

/// <summary>Where an OEM control sits on the device.</summary>
/// <remarks>
/// Placement decides which actions are legal. A rear control may be forwarded to the virtual target's
/// rear paddle because rear paddles already are gameplay input; a front control may not, because
/// making a front OEM button a gameplay button is exactly the general remapping that is out of scope.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OemControlPlacement>))]
public enum OemControlPlacement
{
    /// <summary>A front-facing vendor button, such as a home or quick-settings key.</summary>
    Front,

    /// <summary>A rear paddle or grip control.</summary>
    Rear,
}

/// <summary>What a press of an OEM control may be bound to.</summary>
/// <remarks>
/// A closed vocabulary. There is no entry for an executable, a script, a shell command, a text macro,
/// or an arbitrary key sequence, and adding one would turn OEM assignment into the general macro
/// surface the design excludes.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OemAction>))]
public enum OemAction
{
    /// <summary>Do nothing.</summary>
    Disabled,

    /// <summary>Open or close the WSGM overlay.</summary>
    ToggleWsgmOverlay,

    /// <summary>Open or close Steam's native Quick Access Menu.</summary>
    ToggleSteamQuickAccess,

    /// <summary>Open the overlay directly on the Device page.</summary>
    ShowWsgmDevicePage,

    /// <summary>Open or close the WSGM taskbar.</summary>
    ToggleWsgmTaskbar,

    /// <summary>Switch between Desktop and Game Mode.</summary>
    ToggleDesktopGameMode,

    /// <summary>Show or hide the on-screen keyboard.</summary>
    ToggleOnScreenKeyboard,

    /// <summary>Move to the next performance profile.</summary>
    CyclePerformanceProfile,

    /// <summary>Move to the next performance-overlay level.</summary>
    CyclePerformanceOverlayLevel,

    /// <summary>Forward as the current target's first rear control. Rear placement only.</summary>
    VirtualTargetRearButton1,

    /// <summary>Forward as the current target's second rear control. Rear placement only.</summary>
    VirtualTargetRearButton2,
}

/// <summary>Which press duration an assignment applies to.</summary>
public enum OemPressKind
{
    /// <summary>A short press.</summary>
    Short,

    /// <summary>A long press, where the source distinguishes one.</summary>
    Long,
}

/// <summary>The physical edge represented by an OEM event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OemControlEdge>))]
public enum OemControlEdge
{
    /// <summary>The control became pressed.</summary>
    Pressed,

    /// <summary>The control was released and any held-state guard may reset.</summary>
    Released,
}

/// <summary>One published OEM control event.</summary>
/// <param name="ControlId">The control that was pressed.</param>
/// <param name="Press">Which press duration was observed.</param>
/// <param name="SourceGeneration">Device generation the event came from.</param>
/// <param name="Timestamp">When it was observed, in UTC.</param>
/// <param name="DeduplicationId">
/// Identifier that is equal across every source reporting the same physical press.
/// </param>
/// <param name="Edge">Whether this event represents the press or release edge.</param>
/// <remarks>
/// The deduplication ID exists because one press can legitimately arrive twice: a vendor event
/// channel and a raw-input path may both see it. Without a shared identifier WSGM would toggle the
/// QAM open and closed on a single press.
/// </remarks>
public sealed record OemControlEvent(
    string ControlId,
    OemPressKind Press,
    long SourceGeneration,
    DateTimeOffset Timestamp,
    string DeduplicationId,
    OemControlEdge Edge = OemControlEdge.Pressed);

/// <summary>
/// Decides which OEM actions may be bound to a control.
/// </summary>
public static class OemActionRules
{
    /// <summary>
    /// Whether an action may be assigned to a control in the given placement.
    /// </summary>
    /// <param name="action">The action being assigned.</param>
    /// <param name="placement">Where the control sits on the device.</param>
    /// <returns><see langword="true"/> when the assignment is legal.</returns>
    public static bool IsAssignable(OemAction action, OemControlPlacement placement) =>
        !IsVirtualTargetButton(action) || placement is OemControlPlacement.Rear;

    /// <summary>
    /// Whether an action forwards the press into the virtual controller.
    /// </summary>
    /// <param name="action">The action to classify.</param>
    /// <returns><see langword="true"/> when the press becomes gameplay input.</returns>
    public static bool IsVirtualTargetButton(OemAction action) => action
        is OemAction.VirtualTargetRearButton1
        or OemAction.VirtualTargetRearButton2;

    /// <summary>
    /// Whether a bound action can currently run.
    /// </summary>
    /// <param name="action">The bound action.</param>
    /// <param name="targetHasRearButtons">Whether the active virtual target exposes rear controls.</param>
    /// <param name="reason">Why it cannot, when it cannot.</param>
    /// <returns><see langword="true"/> when the action is currently available.</returns>
    /// <remarks>
    /// Routing is mutually exclusive per press: a press is either forwarded as a rear control or
    /// consumed as a WSGM action, never both. When the selected target has no rear controls the
    /// binding becomes unavailable with a reason rather than silently falling through to something
    /// else, so the user can see why their paddle stopped working.
    /// </remarks>
    public static bool IsAvailable(
        OemAction action,
        bool targetHasRearButtons,
        out CapabilityReason? reason)
    {
        if (IsVirtualTargetButton(action) && !targetHasRearButtons)
        {
            reason = new CapabilityReason(
                CapabilityReasonCode.Unsupported,
                "The selected virtual controller target has no rear controls.");
            return false;
        }

        reason = null;
        return true;
    }
}
