using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using CanonicalControllerSample = LibHandheld.Contracts.CanonicalControllerSample;

namespace WSGM.Shell;

/// <summary>Closed semantic source consumed by the Device overlay destination.</summary>
internal interface IDeviceOverlaySource : IDisposable
{
    /// <summary>Signals that consumers should read a new snapshot; input-rate samples use their separate event.</summary>
    event Action? Changed;

    /// <summary>Raised for each physical sample while the glyph input test is observing.</summary>
    /// <remarks>
    ///     Separate from <see cref="Changed" /> because it fires at input rate. A consumer must treat it
    ///     as a hint to update one visual state, never as a reason to rebuild a page.
    /// </remarks>
    event Action<CanonicalControllerSample>? PhysicalSampleReceived;

    /// <summary>Resolved physical OEM edges while the glyph preview is observing.</summary>
    event Action<GlyphControlId, bool>? PhysicalOemControlReceived;

    /// <summary>Releases preview highlights when device ownership or configuration changes.</summary>
    event Action? PhysicalInputReset;

    /// <summary>The device's own glyph for one navigation hint, when one applies.</summary>
    /// <param name="control">The control the hint names.</param>
    /// <returns>The glyph to draw, or null to keep the written letter.</returns>
    /// <remarks>
    ///     Null is the normal answer on most machines, and the caller must treat it as "show the letter"
    ///     rather than "show nothing". The hint is only replaced when the input actually reaching WSGM
    ///     is the managed handheld's, because a hint showing a Claw button while the user is holding an
    ///     Xbox pad is worse than the letter it replaced.
    /// </remarks>
    PhysicalGlyphRenderPlan? NavigationHint(GlyphControlId control);

    /// <summary>Starts delivering physical samples for the glyph input test.</summary>
    /// <returns>A lease that stops delivery when disposed.</returns>
    /// <remarks>
    ///     Leased rather than always-on: the samples exist to light a preview that is on one page, and
    ///     nothing else on the Device surface wants an input-rate event.
    /// </remarks>
    IDisposable ObservePhysicalSamples();

    /// <summary>Projects coordinator-owned state for the Device surface without hardware I/O.</summary>
    /// <returns>The current presentation snapshot; null optional rows are omitted by the view.</returns>
    DeviceOverlaySnapshot Snapshot();

    /// <summary>Dispatches the row's action or suggested next value through its owning capability router.</summary>
    /// <param name="capability">Snapshot row carrying identity, generations and requested value.</param>
    /// <param name="cancellationToken">Cancels waiting; a dispatched hardware write may still complete.</param>
    /// <returns>Completion after the command attempt and host projection update; does not itself prove hardware readback.</returns>
    Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default);

    /// <summary>Persists the host's physical-button artwork policy and republishes presentation.</summary>
    /// <param name="selection">Declared physical glyph policy.</param>
    /// <param name="cancellationToken">Cancels configuration persistence or reconciliation.</param>
    /// <returns>Completion after the requested host setting is applied; no device firmware is written.</returns>
    Task SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection selection, CancellationToken cancellationToken = default);

    /// <summary>Turns AutoTDP on or off and persists the choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new setting is persisted.</returns>
    Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores AutoTDP's frame-rate target in the layer in force.</summary>
    /// <param name="fps">The target the slider settled on.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task completing once the target is saved.</returns>
    Task SetAutoTdpTargetAsync(int fps, CancellationToken cancellationToken = default);

    /// <summary>Applies one explicit host setting selection without cycling through intermediate writes.</summary>
    /// <param name="rowId">Stable host-owned row ID, such as controller target or authored profile.</param>
    /// <param name="value">Selected machine value; null clears a selection where the row supports it.</param>
    /// <param name="cancellationToken">Cancels persistence or reconciliation waiting.</param>
    /// <returns>Completion after the selection attempt; downstream ownership changes retain their own outcome semantics.</returns>
    Task SetHostSelectionAsync(string rowId, string? value, CancellationToken cancellationToken = default);

    /// <summary>Moves the controller target to the next one in the layer in force and persists it.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new target is persisted and applied.</returns>
    Task CycleControllerTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the running game's value for one setting, so it falls back to Global.</summary>
    /// <param name="overrideId">The id a row carried in its <c>OverrideId</c>.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task completing once the override is gone.</returns>
    Task UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default);

    /// <summary>Requests explicit recovery of a faulted device cycle.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>A task completing once the attempt has been made.</returns>
    Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves to the next authored fan profile, or to none, and applies it.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new selection is persisted and applied.</returns>
    /// <remarks>
    ///     Saved to the running game's profile while it is on, and to Global otherwise, like every
    ///     other per-game value.
    /// </remarks>
    Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default);
}
