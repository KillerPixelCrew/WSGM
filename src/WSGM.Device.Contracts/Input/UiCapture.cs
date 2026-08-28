using System;
using System.Collections.Generic;

namespace WSGM.Device.Contracts.Input;

/// <summary>
/// Where WSGM's own UI is reading controller input from.
/// </summary>
public enum UiInputSource
{
    /// <summary>No source is available; keyboard and touch only.</summary>
    None,

    /// <summary>
    /// The plugin's canonical physical input, read directly.
    /// </summary>
    /// <remarks>
    /// Available when controller management owns a healthy physical source. The device is hidden from
    /// ordinary applications but still readable by the allowlisted host, so WSGM surfaces do not need
    /// to block Steam to read it — and therefore do not acquire the Steam Input surface lease.
    /// </remarks>
    ManagedCanonical,

    /// <summary>
    /// The existing SDL path, with the Steam Input lease acquired as before.
    /// </summary>
    /// <remarks>
    /// Used whenever the managed source is not available: controller management off, an external or
    /// unsupported controller, or a degraded managed source. The lease infrastructure stays a
    /// permanent capability precisely for these cases.
    /// </remarks>
    SdlWithSteamLease,
}

/// <summary>
/// Reference-counted claim on controller input by WSGM's own surfaces.
/// </summary>
/// <remarks>
/// Reference counted because surfaces nest: the overlay can open the taskbar, which can open a
/// picker. Releasing on the first close would hand input back to the game while a WSGM surface is
/// still on screen.
/// <para>
/// This is a WSGM-local capture, not a Steam Input lease. It installs no hook, revokes no HID handle,
/// triggers no Steam controller rescan, and changes no layout — it only decides where already-read
/// input goes.
/// </para>
/// </remarks>
public sealed class UiCaptureState
{
    private readonly HashSet<string> _surfaces = new(StringComparer.Ordinal);
    private CanonicalButtons _suppressedUntilRelease;

    /// <summary>Whether any WSGM surface currently holds capture.</summary>
    public bool IsCaptured => _surfaces.Count > 0;

    /// <summary>How many surfaces hold capture.</summary>
    public int Depth => _surfaces.Count;

    /// <summary>Buttons held when capture began, suppressed until they are released.</summary>
    public CanonicalButtons SuppressedButtons => _suppressedUntilRelease;

    /// <summary>
    /// Claims capture for a surface.
    /// </summary>
    /// <param name="surfaceId">Identifier of the surface claiming capture.</param>
    /// <param name="heldAtOpen">Buttons already held at the moment the surface opened.</param>
    /// <returns><see langword="true"/> when this claim started capture.</returns>
    /// <remarks>
    /// Buttons held at open are suppressed until fully released, so the chord that opened a surface
    /// cannot immediately activate whatever control now has focus underneath it.
    /// </remarks>
    public bool Claim(string surfaceId, CanonicalButtons heldAtOpen)
    {
        bool wasCaptured = IsCaptured;
        _surfaces.Add(surfaceId);

        if (!wasCaptured)
        {
            _suppressedUntilRelease = heldAtOpen;
        }

        return !wasCaptured;
    }

    /// <summary>
    /// Releases one surface's claim.
    /// </summary>
    /// <param name="surfaceId">Identifier of the surface releasing capture.</param>
    /// <returns><see langword="true"/> when the last claim was released.</returns>
    public bool Release(string surfaceId)
    {
        _surfaces.Remove(surfaceId);
        return !IsCaptured;
    }

    /// <summary>
    /// Filters a sample for UI consumption, removing buttons still held from before capture began.
    /// </summary>
    /// <param name="buttons">Buttons in the incoming sample.</param>
    /// <returns>Buttons the UI should act on.</returns>
    /// <remarks>
    /// Suppression clears per button as each is released, not all at once, so a user holding two
    /// controls at open regains each one independently.
    /// </remarks>
    public CanonicalButtons FilterForUi(CanonicalButtons buttons)
    {
        _suppressedUntilRelease &= buttons;
        return buttons & ~_suppressedUntilRelease;
    }

    /// <summary>
    /// Whether input may resume flowing to the virtual target.
    /// </summary>
    /// <param name="buttons">Buttons currently held.</param>
    /// <returns><see langword="true"/> when forwarding can resume on a clean boundary.</returns>
    /// <remarks>
    /// Forwarding resumes only once every control used by the UI has been released. Resuming while a
    /// button is still down would deliver a press the game never saw the start of — the closing press
    /// of a WSGM surface arriving in the game as a fresh input.
    /// </remarks>
    public bool CanResumeForwarding(CanonicalButtons buttons)
    {
        if (IsCaptured)
        {
            return false;
        }

        _suppressedUntilRelease &= buttons;
        return _suppressedUntilRelease == CanonicalButtons.None;
    }
}

/// <summary>
/// The mandatory triggers for sending a neutral state to the virtual target.
/// </summary>
/// <remarks>
/// Each of these leaves the target with no one driving it. Without an explicit zero, the last
/// forwarded sample stays latched and the game keeps seeing whatever was held at that instant — a
/// stuck trigger or a walking character with no input.
/// </remarks>
[Flags]
public enum ZeroOutputTrigger
{
    /// <summary>No reason to zero.</summary>
    None = 0,

    /// <summary>A WSGM surface claimed input capture.</summary>
    UiCaptureClaimed = 1 << 0,

    /// <summary>The virtual target was removed or replaced.</summary>
    TargetRemoved = 1 << 1,

    /// <summary>The foreground game exited.</summary>
    GameExited = 1 << 2,

    /// <summary>The system is suspending.</summary>
    Suspending = 1 << 3,

    /// <summary>The physical controller disconnected.</summary>
    PhysicalDisconnected = 1 << 4,

    /// <summary>Controller management or the plugin was disabled.</summary>
    PluginDisabled = 1 << 5,

    /// <summary>The input source faulted.</summary>
    SourceFaulted = 1 << 6,

    /// <summary>The input source is being switched.</summary>
    SourceSwitching = 1 << 7,
}
