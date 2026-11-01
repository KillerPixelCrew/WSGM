using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Describes a pairing question for the UI to render.</summary>
/// <param name="Token">Identifies the request when answering.</param>
/// <param name="Kind">
///     Which ceremony to present.
///     <see cref="WindowsRadio.PairingKind.Unknown" /> is deliberately presented as confirm-only
///     rather than declined: an accept is what Windows most often wants, and the log line records
///     the raw kind so a device that really needs another ceremony is still diagnosable.
/// </param>
/// <param name="Pin">The PIN to show, for display-pin and confirm-pin-match.</param>
/// <param name="DeviceName">The device being paired.</param>
public readonly record struct PairingPrompt(
    uint Token,
    WindowsRadio.PairingKind Kind,
    string Pin,
    string DeviceName);
