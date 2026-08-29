namespace WSGM.Device.Sdk.Ipc;

/// <summary>
/// Every message the device protocol can carry.
/// </summary>
/// <remarks>
/// A closed enumeration, and that is the entire security boundary of the IPC surface. There is no
/// message type for executing a command, running a shell, opening a file, invoking a WMI method,
/// sending a HID report, reading an EC register, issuing an IOCTL, running a script, resolving a
/// path, calling a helper, or passing a raw buffer — so a compromised or malicious peer cannot ask
/// for one. Those operations are not rejected by a check that could be bypassed or forgotten; they
/// are not expressible.
/// <para>
/// Adding a member here is a deliberate widening of that surface and belongs in review, not in a
/// convenience change. A capability that seems to need a passthrough needs a semantic capability
/// instead.
/// </para>
/// </remarks>
public enum DeviceMessageType : ushort
{
    /// <summary>Reserved. A frame carrying zero is malformed.</summary>
    None = 0,

    // Handshake.

    /// <summary>Host to WSGM: exact-version package/session identity and startup nonce.</summary>
    Hello = 1,

    /// <summary>WSGM to host: acceptance or refusal of that exact-version hello.</summary>
    HelloAck = 2,

    // Lifecycle.

    /// <summary>The host reports its current cycle state.</summary>
    LifecycleState = 10,

    /// <summary>WSGM asks the host to start the plugin.</summary>
    Start = 11,

    /// <summary>WSGM asks the host to stop and release everything it owns.</summary>
    Stop = 12,

    /// <summary>WSGM asks the host to quiesce for suspend or lock.</summary>
    Suspend = 13,

    /// <summary>WSGM asks the host to resume after suspend.</summary>
    Resume = 14,

    // Capabilities.

    /// <summary>A complete descriptor set for a new descriptor generation.</summary>
    DescriptorSet = 20,

    /// <summary>One capability state update.</summary>
    StateDelta = 21,

    /// <summary>A request to change or invoke a capability.</summary>
    Command = 22,

    /// <summary>The result of a command.</summary>
    CommandResult = 23,

    /// <summary>A request to abandon an in-flight command.</summary>
    CancelCommand = 24,

    // Controller.

    /// <summary>Physical device identities WSGM needs in order to write HidHide entries.</summary>
    PhysicalIdentities = 30,

    /// <summary>A logical OEM control event.</summary>
    OemEvent = 31,

    /// <summary>An output frame travelling back to the physical device.</summary>
    HapticOutput = 32,

    /// <summary>A step of the two-phase controller handoff.</summary>
    ControllerHandoff = 33,

    /// <summary>The closed set of assignable logical OEM controls.</summary>
    OemControls = 34,

    /// <summary>Enable or release only physical-controller management while the cycle continues.</summary>
    ControllerManagement = 35,

    // Diagnostics.

    /// <summary>A request for a read-only diagnostics snapshot.</summary>
    DiagnosticsRequest = 40,

    /// <summary>A read-only diagnostics snapshot.</summary>
    DiagnosticsSnapshot = 41,

    /// <summary>A structured error answering a specific request.</summary>
    Error = 50,

    /// <summary>A bounded acknowledgment for a semantic request with no richer result.</summary>
    OperationAck = 51,
}
