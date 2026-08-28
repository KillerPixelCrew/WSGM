using System.Collections.Generic;

namespace WSGM.DeviceLab.Core.Inventory;

/// <summary>One display adapter identity used only for catalog matching.</summary>
public sealed record GraphicsAdapterInventory
{
    /// <summary>PnP instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Adapter marketing name.</summary>
    public string? Name { get; init; }

    /// <summary>PCI vendor identifier.</summary>
    public string? VendorId { get; init; }

    /// <summary>PCI device identifier.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Installed driver version.</summary>
    public string? DriverVersion { get; init; }
}

/// <summary>Access result for an enumerated passive endpoint.</summary>
public enum InventoryAccess
{
    /// <summary>The endpoint metadata was available.</summary>
    Available,

    /// <summary>The endpoint exists but metadata access was denied.</summary>
    AccessDenied,

    /// <summary>The endpoint disappeared during enumeration.</summary>
    Disconnected,

    /// <summary>The platform cannot expose this property without active probing.</summary>
    Unsupported,
}

/// <summary>A serial-port framing value reported by the installed driver.</summary>
/// <remarks>This is a passive observation, not an instruction to transmit with these settings.</remarks>
public sealed record SerialFramingCandidate
{
    /// <summary>Baud rate reported by the provider.</summary>
    public uint? BaudRate { get; init; }

    /// <summary>Number of data bits.</summary>
    public byte? DataBits { get; init; }

    /// <summary>Provider parity value.</summary>
    public byte? Parity { get; init; }

    /// <summary>Provider stop-bit value.</summary>
    public byte? StopBits { get; init; }

    /// <summary>Where this candidate came from.</summary>
    public required string Source { get; init; }
}

/// <summary>One passively enumerated COM endpoint.</summary>
public sealed record SerialEndpointInventory
{
    /// <summary>PnP instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Windows port name, such as <c>COM4</c>.</summary>
    public string? PortName { get; init; }

    /// <summary>Display name.</summary>
    public string? Name { get; init; }

    /// <summary>Provider or device manufacturer.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Physical location when available.</summary>
    public string? LocationPath { get; init; }

    /// <summary>Whether passive metadata was accessible.</summary>
    public required InventoryAccess Access { get; init; }

    /// <summary>Driver-reported framing candidates. No serial handle was opened.</summary>
    public IReadOnlyList<SerialFramingCandidate> FramingCandidates { get; init; } = [];
}

/// <summary>One sensor-like PnP endpoint and its passive association data.</summary>
public sealed record SensorEndpointInventory
{
    /// <summary>PnP instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Friendly endpoint name.</summary>
    public string? Name { get; init; }

    /// <summary>Observed PnP class or sensor kind.</summary>
    public string? Kind { get; init; }

    /// <summary>Parent or container association, private until redacted.</summary>
    public string? AssociationId { get; init; }

    /// <summary>Minimum supported report interval when passively published.</summary>
    public uint? MinimumReportIntervalMilliseconds { get; init; }

    /// <summary>Reported measurement unit, when published.</summary>
    public string? Unit { get; init; }

    /// <summary>Current metadata accessibility.</summary>
    public required InventoryAccess Access { get; init; }
}

/// <summary>Supported independent input views.</summary>
public enum InputBackendKind
{
    /// <summary>Windows XInput slots.</summary>
    XInput,

    /// <summary>DirectInput-compatible PnP devices.</summary>
    DirectInput,

    /// <summary>SDL runtime discovery.</summary>
    Sdl,

    /// <summary>Win32 Raw Input devices.</summary>
    RawInput,

    /// <summary>Raw HID PnP interfaces.</summary>
    RawHid,
}

/// <summary>One endpoint visible through an input backend.</summary>
public sealed record InputEndpointInventory
{
    /// <summary>Backend-local stable slot or session identifier.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Backend-reported display name.</summary>
    public string? Name { get; init; }

    /// <summary>Associated PnP instance when available.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Backend-specific device type.</summary>
    public string? DeviceType { get; init; }

    /// <summary>Whether the endpoint was connected at enumeration time.</summary>
    public bool Connected { get; init; }
}

/// <summary>One independent input backend view.</summary>
public sealed record InputBackendInventory
{
    /// <summary>Backend identity.</summary>
    public required InputBackendKind Backend { get; init; }

    /// <summary>Whether safe enumeration was available.</summary>
    public required InventoryAccess Access { get; init; }

    /// <summary>Observed endpoints in deterministic order.</summary>
    public IReadOnlyList<InputEndpointInventory> Endpoints { get; init; } = [];

    /// <summary>Explicit limit of this view.</summary>
    public string? Limitation { get; init; }
}

/// <summary>Signature observation for a native file.</summary>
public enum BinarySignatureState
{
    /// <summary>An embedded signer certificate was present.</summary>
    Signed,

    /// <summary>No embedded signer certificate was found.</summary>
    Unsigned,

    /// <summary>The signature could not be inspected.</summary>
    Unknown,
}

/// <summary>Native PE metadata read from disk without loading the binary.</summary>
public sealed record NativeBinaryInventory
{
    /// <summary>Absolute file path; private captures only.</summary>
    public required string Path { get; init; }

    /// <summary>File name.</summary>
    public required string Name { get; init; }

    /// <summary>File version resource.</summary>
    public string? Version { get; init; }

    /// <summary>PE architecture.</summary>
    public string? Architecture { get; init; }

    /// <summary>Lowercase SHA-256 of the bytes on disk.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Embedded signature observation; trust is not inferred.</summary>
    public required BinarySignatureState Signature { get; init; }

    /// <summary>Signer certificate subject when present.</summary>
    public string? SignerSubject { get; init; }

    /// <summary>PE export names parsed from disk without invoking them.</summary>
    public IReadOnlyList<string> Exports { get; init; } = [];
}

/// <summary>Relevant running-process observation.</summary>
public sealed record ProcessInventory
{
    /// <summary>Session-local process identifier.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Executable name.</summary>
    public required string Name { get; init; }

    /// <summary>Executable path when accessible; private captures only.</summary>
    public string? Path { get; init; }

    /// <summary>Command line when accessible; private captures only.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Relevant native modules already loaded by the process.</summary>
    public IReadOnlyList<string> LoadedModulePaths { get; init; } = [];
}

/// <summary>Relevant Windows service observation.</summary>
public sealed record ServiceInventory
{
    /// <summary>Service name.</summary>
    public required string Name { get; init; }

    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Observed service state.</summary>
    public string? State { get; init; }

    /// <summary>Configured binary path; private captures only.</summary>
    public string? PathName { get; init; }

    /// <summary>Process ID when running.</summary>
    public int? ProcessId { get; init; }
}

/// <summary>Relevant scheduled-task observation.</summary>
public sealed record ScheduledTaskInventory
{
    /// <summary>Task path and name.</summary>
    public required string Path { get; init; }

    /// <summary>Observed task state.</summary>
    public string? State { get; init; }

    /// <summary>Whether the task is enabled.</summary>
    public bool? Enabled { get; init; }
}

/// <summary>Strength of evidence that another owner conflicts with a resource.</summary>
public enum ConflictEvidenceKind
{
    /// <summary>A name match only; this is never ownership proof.</summary>
    PresenceOnly,

    /// <summary>The resource reported sharing or access denial during an allowlisted read.</summary>
    ExclusiveAccessDenied,

    /// <summary>The production owner explicitly reported an active lease.</summary>
    ReportedLease,
}

/// <summary>One potential or demonstrated resource conflict.</summary>
public sealed record ResourceConflictInventory
{
    /// <summary>Semantic resource ID.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Observed possible owner.</summary>
    public required string Owner { get; init; }

    /// <summary>Evidence strength.</summary>
    public required ConflictEvidenceKind Evidence { get; init; }

    /// <summary>Whether the evidence proves current ownership.</summary>
    public bool Demonstrated => Evidence is not ConflictEvidenceKind.PresenceOnly;
}
