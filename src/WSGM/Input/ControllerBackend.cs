using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>The virtual controller shapes WSGM can present.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VirtualTargetKind>))]
public enum VirtualTargetKind
{
    /// <summary>Valve's composite Steam Deck controller: the richest handheld model.</summary>
    SteamDeckComposite,

    /// <summary>Xbox 360, for native XInput compatibility with older software.</summary>
    Xbox360,

    /// <summary>DualShock 4, for software requiring a PlayStation controller.</summary>
    DualShock4,
}

/// <summary>What a virtual target can consume.</summary>
/// <remarks>
/// A target takes only what it genuinely supports. Nothing is synthesized to fill a gap: gyro reaches
/// a target with native motion and is simply absent elsewhere, and rear paddles reach a target that
/// has them.
/// </remarks>
public sealed record VirtualTargetProfile
{
    /// <summary>Which target this describes.</summary>
    public required VirtualTargetKind Kind { get; init; }

    /// <summary>Whether the target exposes rear controls.</summary>
    public bool SupportsRearButtons { get; init; }

    /// <summary>Whether the target carries native motion input.</summary>
    public bool SupportsMotion { get; init; }

    /// <summary>Whether the target exposes touchpads.</summary>
    public bool SupportsTouchpads { get; init; }

    /// <summary>Whether the target reports capacitive stick touch.</summary>
    public bool SupportsStickTouch { get; init; }

    /// <summary>Whether the target has a dedicated quick-access button.</summary>
    public bool SupportsQuickAccess { get; init; }

    /// <summary>The Steam Deck composite target.</summary>
    public static VirtualTargetProfile SteamDeck { get; } = new()
    {
        Kind = VirtualTargetKind.SteamDeckComposite,
        SupportsRearButtons = true,
        SupportsMotion = true,
        SupportsTouchpads = true,
        SupportsStickTouch = true,
        SupportsQuickAccess = true,
    };

    /// <summary>The Xbox 360 target.</summary>
    public static VirtualTargetProfile Xbox360 { get; } = new()
    {
        Kind = VirtualTargetKind.Xbox360,
    };

    /// <summary>The DualShock 4 target.</summary>
    public static VirtualTargetProfile DualShock4 { get; } = new()
    {
        Kind = VirtualTargetKind.DualShock4,
        SupportsMotion = true,
        SupportsTouchpads = true,
    };

    /// <summary>
    /// Removes controls the target cannot represent.
    /// </summary>
    /// <param name="buttons">Buttons from the canonical sample.</param>
    /// <returns>Buttons this target can carry.</returns>
    /// <remarks>
    /// Dropped, never remapped onto something else. Forwarding a rear paddle as a face button on a
    /// target without paddles would silently make the paddle press a different control in the game.
    /// </remarks>
    public CanonicalButtons Consume(CanonicalButtons buttons)
    {
        if (!SupportsRearButtons)
        {
            buttons &= ~(CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2
                | CanonicalButtons.RearPaddle3 | CanonicalButtons.RearPaddle4);
        }

        if (!SupportsStickTouch)
        {
            buttons &= ~(CanonicalButtons.LeftStickTouch | CanonicalButtons.RightStickTouch);
        }

        if (!SupportsTouchpads)
        {
            buttons &= ~(CanonicalButtons.LeftPadTouch | CanonicalButtons.RightPadTouch
                | CanonicalButtons.LeftPadClick | CanonicalButtons.RightPadClick);
        }

        if (!SupportsQuickAccess)
        {
            buttons &= ~CanonicalButtons.QuickAccess;
        }

        return buttons;
    }
}

/// <summary>
/// The WSGM-owned virtual-controller backend.
/// </summary>
/// <remarks>
/// The seam that keeps HIDMaestro replaceable. Device plugins never call a backend directly; they
/// publish canonical state and consume canonical output, so swapping the backend does not touch a
/// single community plugin.
/// </remarks>
public interface IControllerBackend
{
    /// <summary>Targets this backend can create on the current machine.</summary>
    IReadOnlyList<VirtualTargetKind> SupportedTargets { get; }

    /// <summary>
    /// Creates the selected virtual target.
    /// </summary>
    /// <param name="kind">Which target to create.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>The generation of the created target.</returns>
    /// <remarks>
    /// Only one target exists at a time. Several connected at once risks duplicate input, unstable
    /// XInput slot assignment, and ambiguous application binding.
    /// </remarks>
    Task<long> CreateTargetAsync(VirtualTargetKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the current target.
    /// </summary>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task completing when the target is gone.</returns>
    Task RemoveTargetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends one canonical sample to the current target.
    /// </summary>
    /// <param name="sample">The sample to forward.</param>
    /// <param name="targetGeneration">The generation the caller believes is current.</param>
    /// <returns><see langword="true"/> when the sample was delivered.</returns>
    bool Submit(CanonicalControllerSample sample, long targetGeneration);
}

/// <summary>
/// The controller input WSGM's own surfaces navigate from.
/// </summary>
/// <remarks>
/// An abstraction over SDL rather than SDL itself, so the managed canonical source and the SDL
/// fallback are interchangeable behind one seam. In managed mode the matching physical and virtual
/// SDL devices are ignored, because the same press would otherwise arrive twice — once from the
/// canonical source and once from SDL seeing the virtual target.
/// </remarks>
public interface IUiGamepadSource
{
    /// <summary>Where this source reads from.</summary>
    UiInputSource Kind { get; }

    /// <summary>Whether the source is currently delivering usable input.</summary>
    bool IsHealthy { get; }

    /// <summary>Generation of the underlying device, so consumers can detect a swap.</summary>
    long SourceGeneration { get; }

    /// <summary>Raised for each complete state sample.</summary>
    event EventHandler<CanonicalControllerSample>? SampleReceived;

    /// <summary>Raised when the source becomes healthy or stops being healthy.</summary>
    event EventHandler<bool>? HealthChanged;
}
