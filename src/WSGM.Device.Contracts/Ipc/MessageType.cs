namespace WSGM.Device.Contracts.Ipc;

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

    /// <summary>Host to WSGM: offered protocol versions and schema fingerprint.</summary>
    Hello = 1,

    /// <summary>WSGM to host: the negotiated version, or a refusal.</summary>
    HelloAck = 2,

    // Lifecycle.

    /// <summary>The host reports its current cycle state.</summary>
    LifecycleState = 10,

    /// <summary>WSGM asks the host to begin activation.</summary>
    Activate = 11,

    /// <summary>WSGM asks the host to release everything it owns.</summary>
    Deactivate = 12,

    /// <summary>WSGM asks the host to quiesce for suspend or lock.</summary>
    Suspend = 13,

    /// <summary>WSGM asks the host to resume after suspend.</summary>
    Resume = 14,

    /// <summary>Per-resource state changed.</summary>
    ResourceState = 15,

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

    // Diagnostics.

    /// <summary>A request for a read-only diagnostics snapshot.</summary>
    DiagnosticsRequest = 40,

    /// <summary>A read-only diagnostics snapshot.</summary>
    DiagnosticsSnapshot = 41,

    /// <summary>A structured error answering a specific request.</summary>
    Error = 50,
}

/// <summary>What to do with a frame that cannot be handled.</summary>
/// <remarks>
/// Distinguished because they are not equally serious. An unknown message type from a peer inside the
/// negotiated version window is survivable — it is what forward compatibility looks like — while a
/// malformed frame means the stream is no longer trustworthy and cannot be resynchronized by
/// guessing where the next frame starts.
/// </remarks>
public enum UnknownMessageResponse
{
    /// <summary>Ignore the frame and continue. Used for unknown notifications.</summary>
    Ignore,

    /// <summary>Answer with an error and continue. Used for unknown requests.</summary>
    ReplyWithError,

    /// <summary>Close the connection. Used when the stream itself cannot be trusted.</summary>
    Disconnect,
}
