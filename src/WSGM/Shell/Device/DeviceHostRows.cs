using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>The device page's host-owned rows: controller, glyphs, profiles, AutoTDP and recovery.</summary>
/// <remarks>Pure formatting shared by the overlay bridge, the simulated source and Steam's settings page.</remarks>
internal static class DeviceHostRows
{
    /// <summary>One choice of a host-owned row.</summary>
    /// <param name="value">The value the row reports.</param>
    /// <param name="label">What the user reads.</param>
    /// <returns>The choice.</returns>
    internal static CapabilityChoice HostChoice(string value, string label)
    {
        return new CapabilityChoice(value,
            new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label });
    }

    /// <summary>The choices of an authored-profile row: the empty choice first, then every profile.</summary>
    /// <param name="profiles">Profiles authored for this device, or null without any.</param>
    /// <param name="emptyLabel">The empty choice's label: None, or Use Global while a game profile is edited.</param>
    /// <returns>The choices in presentation order.</returns>
    internal static CapabilityChoice[] ProfileChoices(IReadOnlyList<DeviceAuthoredProfile>? profiles,
        string emptyLabel)
    {
        return
        [
            HostChoice(string.Empty, emptyLabel),
            .. profiles?.Select(profile => HostChoice(profile.ProfileId, profile.Name)) ?? []
        ];
    }

    /// <summary>The empty lighting-profile choice's label for the current editing scope.</summary>
    /// <param name="editsGame">Whether the running game's own profile is being edited.</param>
    /// <returns>Use Global for a game profile, otherwise None.</returns>
    internal static string LightingProfileEmptyLabel(bool editsGame)
    {
        return editsGame ? "Use Global" : "None";
    }

    /// <summary>Projects controller management into the Controller and motion page's own row.</summary>
    /// <param name="enabled">Whether management may run at all.</param>
    /// <param name="status">The manager's truthful state.</param>
    /// <returns>The row, or null when management is off and there is nothing to show.</returns>
    /// <remarks>
    ///     A direct row rather than a synthesized capability, like the AutoTDP and glyph rows: the target
    ///     is WSGM's own setting, so routing it through the plugin capability dispatch would mean a
    ///     second meaning for a capability id and a branch inside the one invoke path.
    /// </remarks>
    internal static DescriptorRow? ControllerView(
        bool enabled,
        ControllerManagerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!enabled)
        {
            // Off is a setting, not a fault, and the page has other rows. Saying nothing here is
            // better than a permanently greyed control the user cannot act on from this page.
            return null;
        }

        var health = status.State switch
        {
            ControllerManagementState.Active or ControllerManagementState.Native => DescriptorStatus.Available,
            ControllerManagementState.Idle => DescriptorStatus.Stale,
            ControllerManagementState.Faulted => DescriptorStatus.Warning,
            ControllerManagementState.Unavailable => DescriptorStatus.Unsupported,
            _ => DescriptorStatus.None
        };
        var trailing = status.Target is { } target ? TargetLabel(target) : "NONE";
        var description = status.Detail;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = status.State switch
            {
                ControllerManagementState.Active => "A virtual controller is present and receiving input",
                ControllerManagementState.Idle => "Ready · no virtual controller is present yet",
                _ => "Present the physical controller as a chosen virtual one"
            };
        }

        if (status.ApplicationId is { Length: > 0 })
        {
            // A game holds the target it launched with, so the row has to say that a change will not
            // reach the running one. Without this the control looks broken.
            description += " · restart the running game to change its target";
        }

        return new DescriptorRow(
            DeviceHostRowIds.ControllerTarget,
            "Controller target",
            description,
            trailing,
            // Only when a change can actually take effect. Cycling into a target the backend cannot
            // bring up would replace one broken state with another.
            status.State is not ControllerManagementState.Unavailable,
            health);
    }

    /// <summary>Builds the glyph preview from the resolved profile and the glyph service.</summary>
    /// <param name="selection">The resolved physical-glyph selection.</param>
    /// <param name="glyphs">The service that turns the profile's artwork into drawable geometry.</param>
    /// <param name="inputTestAvailable">Whether physical input is reaching the surface.</param>
    /// <returns>The preview, or null when no profile supplies anything to draw.</returns>
    /// <remarks>
    ///     Only controls the profile says are present are shown. A profile that declares a control
    ///     absent is describing the hardware — an MSI Claw has no trackpads — so drawing a placeholder
    ///     for it would contradict the thing the preview exists to confirm.
    ///     <para>
    ///         The service is asked with the <c>DeviceDescription</c> surface, which is authorized
    ///         unconditionally, because this preview is a description of the device rather than a
    ///         navigation hint or a Steam route: it has to render the plugin's artwork even when the active
    ///         input source is not the managed handheld, which is exactly when someone is checking it.
    ///     </para>
    /// </remarks>
    internal static DeviceOverlayGlyphPreview? GlyphPreview(
        PhysicalGlyphSelectionResult selection,
        PhysicalGlyphPlans glyphs,
        bool inputTestAvailable)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(glyphs);
        if (selection.Profile is not { } profile)
        {
            return null;
        }

        List<DeviceOverlayGlyphPreviewItem> items = [];
        foreach (var mapping in profile.Manifest.Controls)
        {
            if (mapping.Presence is not GlyphControlPresence.Present)
            {
                continue;
            }

            var plan = glyphs.Resolve(
                selection,
                mapping.Control,
                PhysicalGlyphSurface.DeviceDescription,
                true);
            if (!plan.UsesDeviceArtwork)
            {
                continue;
            }

            items.Add(new DeviceOverlayGlyphPreviewItem(
                mapping.Control,
                // The label printed on the device wins over the canonical name: the preview exists
                // to be compared against the hardware in the user's hands.
                string.IsNullOrWhiteSpace(mapping.PhysicalLabel)
                    ? ControlLabel(mapping.Control)
                    : mapping.PhysicalLabel,
                plan));
        }

        if (items.Count == 0)
        {
            return null;
        }

        return new DeviceOverlayGlyphPreview(
            profile.Manifest.DisplayName,
            $"{items.Count} controls · revision {profile.Manifest.Revision} · source {Short(profile.Manifest.SourceRevision)}",
            items,
            inputTestAvailable);
    }

    /// <summary>Turns a canonical control id into a readable name.</summary>
    /// <param name="control">The control.</param>
    /// <returns>The name, with word boundaries restored.</returns>
    /// <remarks>
    ///     Derived from the enum rather than tabulated, so a control added to the SDK gets a sensible
    ///     name here without a second list to forget to update. A profile that prints its own label on
    ///     the device overrides this anyway.
    /// </remarks>
    private static string ControlLabel(GlyphControlId control)
    {
        var name = control.ToString();
        StringBuilder text = new(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(name[index - 1]))
            {
                text.Append(' ');
            }

            text.Append(character);
        }

        return text.ToString();
    }

    private static string Short(string revision)
    {
        return revision.Length <= 12
            ? revision
            : revision[..12];
    }

    /// <summary>Projects the authored profile in force into its overlay row.</summary>
    /// <param name="profiles">Profiles authored for this device.</param>
    /// <param name="selectedProfileId">The profile currently chosen, or null for none.</param>
    /// <param name="source">Which profile layer the choice came from.</param>
    /// <returns>The row, or null when the device has no authored profiles at all.</returns>
    /// <remarks>
    ///     Null when nothing has been authored: profiles are created in Settings, and a row offering a
    ///     choice between nothing would be an invitation to press a button that cannot do anything.
    ///     <para>
    ///         The scope is in the description rather than implied, because a profile chosen for one game
    ///         and the same profile chosen for everything read identically otherwise — and the difference is
    ///         what the user changes when they open this row mid-game.
    ///     </para>
    /// </remarks>
    internal static DescriptorRow? AuthoredProfileView(
        IReadOnlyList<DeviceAuthoredProfile> profiles,
        string? selectedProfileId,
        ProfileSource source)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
        {
            return null;
        }

        var display = profiles[0].CapabilityId switch
        {
            CapabilityIds.FanCurve => new CapabilityDisplay { Key = DisplayKey.FanCurve },
            CapabilityIds.LightingColor => new CapabilityDisplay { Key = DisplayKey.Lighting },
            _ => new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Device profile" }
        };
        var label = CapabilityDisplayLabels.For(display, "Device profile");

        var selected = selectedProfileId is { Length: > 0 }
            ? profiles.FirstOrDefault(profile => string.Equals(
                profile.ProfileId,
                selectedProfileId,
                StringComparison.Ordinal))
            : null;

        if (selectedProfileId is { Length: > 0 } && selected is null)
        {
            // The stored choice names a profile that no longer exists. Said plainly rather than
            // shown as "none", because none is a state the user chose and this is not.
            return new DescriptorRow(
                DeviceHostRowIds.AuthoredProfile,
                label,
                // Cyclable on purpose: pressing it moves to a profile that does exist, which is the
                // fastest way out of the state for a user who is mid-game.
                "The selected profile was deleted · press to choose another",
                "MISSING",
                true,
                DescriptorStatus.Warning);
        }

        var scope = selected is null
            ? $"{profiles.Count} authored · none selected"
            : source is ProfileSource.Game
                ? $"1 of {profiles.Count} · this game's override"
                : $"1 of {profiles.Count} · from Global";

        return new DescriptorRow(
            DeviceHostRowIds.AuthoredProfile,
            label,
            scope,
            selected is null ? "NONE" : selected.Name.ToUpperInvariant(),
            true,
            selected is null ? DescriptorStatus.None : DescriptorStatus.Available);
    }

    internal static DescriptorRow LightingProfileView(IReadOnlyList<DeviceAuthoredProfile> profiles,
        Resolved<string?> selected, string? detail, DescriptorStatus status)
    {
        var row = AuthoredProfileView(profiles, selected.Value, selected.Source)
                  ?? new DescriptorRow(DeviceHostRowIds.LightingProfile, "Lighting profile",
                      "Create a color profile in Settings, Device profiles.", "NONE", false);
        return row with
        {
            Id = DeviceHostRowIds.LightingProfile,
            Title = "Lighting profile",
            Description = detail is null ? row.Description : row.Description + " · " + detail,
            Status = status == DescriptorStatus.None ? row.Status : status,
            OverrideId = selected.IsGameOverride ? nameof(ProfileField.LightingProfile) : null
        };
    }

    /// <summary>Projects the device cycle's recoverable state into the Diagnostics page's own row.</summary>
    /// <param name="state">The current cycle state.</param>
    /// <returns>The row, or null when the cycle is healthy and there is nothing to recover.</returns>
    /// <remarks>
    ///     Absent when healthy. A recovery control that is always present but almost always inert trains
    ///     a user to ignore it, which is the opposite of what it is for.
    /// </remarks>
    internal static DescriptorRow? RecoveryView(DeviceCycleState state)
    {
        if (state is not DeviceCycleState.Faulted)
        {
            return null;
        }

        return new DescriptorRow(
            DeviceHostRowIds.Retry,
            "Retry device integration",
            $"{LifecycleLabel(state)} · starts one manual recovery attempt",
            "READY",
            true,
            DescriptorStatus.Warning);
    }

    /// <summary>
    ///     Every controller target in the order both surfaces offer it, with the label its choice
    ///     carries.
    /// </summary>
    /// <remarks>
    ///     Steam Deck first, because it is the target that carries every control the canonical model
    ///     defines; the other two exist for compatibility with software that does not understand it.
    /// </remarks>
    internal static readonly IReadOnlyList<(ManagedControllerTarget Target, string Label)> Targets =
    [
        (ManagedControllerTarget.SteamDeckComposite, "Steam Deck"),
        (ManagedControllerTarget.Xbox360, "Xbox 360"),
        (ManagedControllerTarget.DualShock4, "DualShock 4")
    ];

    /// <summary>The controller-target choices the backend can build, in <see cref="Targets" /> order.</summary>
    /// <param name="supported">Targets the backend can build.</param>
    /// <returns>The offered targets with their labels.</returns>
    /// <remarks>
    ///     Unsupported targets are left out rather than offered: selecting one persists a target the
    ///     backend then refuses to create, which leaves controller management unavailable.
    /// </remarks>
    internal static (ManagedControllerTarget Target, string Label)[] TargetChoices(
        IReadOnlyList<ManagedControllerTarget> supported)
    {
        return [.. Targets.Where(option => supported.Contains(option.Target))];
    }

    /// <summary>The short badge the controller row shows for its target.</summary>
    /// <param name="target">The target in effect.</param>
    /// <returns>The badge text.</returns>
    internal static string TargetLabel(ManagedControllerTarget target)
    {
        return target switch
        {
            ManagedControllerTarget.SteamDeckComposite => "DECK",
            ManagedControllerTarget.Xbox360 => "XBOX",
            ManagedControllerTarget.DualShock4 => "DS4",
            _ => "NONE"
        };
    }

    /// <summary>Projects AutoTDP's switch and live state into one row.</summary>
    /// <param name="enabled">The persisted setting.</param>
    /// <param name="status">Live state, or null when the service is not running.</param>
    /// <param name="availability">The service's authoritative admission state.</param>
    /// <returns>The row.</returns>
    /// <remarks>
    ///     The row reports what AutoTDP is actually doing, not merely that it is switched on. A user who
    ///     turned it on and sees nothing happening needs to know whether it is waiting for a game, held
    ///     by a manual power change, or unable to find a power limit at all.
    /// </remarks>
    internal static DescriptorRow AutoTdpView(
        bool enabled, AutoTdpStatus? status, AutoTdpAvailability? availability = null)
    {
        if (availability is { Available: false })
        {
            return new DescriptorRow(DeviceHostRowIds.AutoTdp, "AutoTDP", availability.Detail, "OFF",
                false, DescriptorStatus.Unsupported);
        }

        if (!enabled)
        {
            return new DescriptorRow(
                DeviceHostRowIds.AutoTdp,
                "AutoTDP",
                "Move the power limit from measured frame delivery",
                "OFF",
                true);
        }

        var detail = status is null ? "Starting." : AutoTdpReason.Describe(status.Detail);
        var trailing = status?.Watts is { } watts
            ? watts.ToString(CultureInfo.InvariantCulture) + " W"
            : "ON";
        var health = status?.State switch
        {
            AutoTdpState.Controlling => DescriptorStatus.Available,
            AutoTdpState.Paused => DescriptorStatus.Warning,
            AutoTdpState.Unavailable => DescriptorStatus.Unsupported,
            AutoTdpState.Idle => DescriptorStatus.Stale,
            _ => DescriptorStatus.None
        };
        if (status is { FrametimeMs: { } frametime, TargetFrametimeMs: { } target })
        {
            // Invariant, like the watts above it. The surrounding sentence is English, and a row
            // that mixed a comma decimal separator with a full stop in one line would read as a
            // formatting bug rather than as localisation.
            detail = string.Create(
                CultureInfo.InvariantCulture,
                $"{frametime:F1} ms against a {target:F1} ms deadline · {detail}");
        }

        return new DescriptorRow(DeviceHostRowIds.AutoTdp, "AutoTDP", detail, trailing, true, health);
    }

    /// <summary>Projects AutoTDP's frame-rate target as the slider under its switch.</summary>
    /// <param name="enabled">The persisted AutoTDP setting.</param>
    /// <param name="target">The resolved target.</param>
    /// <returns>The row while AutoTDP is on, else null: the target means nothing without control.</returns>
    /// <remarks>
    ///     Its own profile value, so AutoTDP runs without a frame limit. The Quick Access slider is fed the
    ///     same <see cref="AutoTdpTargetView" />, so both draw the same value, bounds, marker and line.
    /// </remarks>
    internal static DescriptorRow? AutoTdpTargetRow(bool enabled, AutoTdpTargetView target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return enabled
            ? new DescriptorRow(DeviceHostRowIds.AutoTdpTarget, "Target frame rate", target.Description,
                string.Create(CultureInfo.InvariantCulture, $"{target.Fps} FPS"), true, DescriptorStatus.Available)
            {
                Range = new DescriptorRange(AutoTdpTarget.MinimumFps, AutoTdpTarget.MaximumFps, 1),
                Value = target.Fps,
                OverrideId = target.GameOverride ? nameof(ProfileField.AutoTdpTarget) : null
            }
            : null;
    }

    /// <summary>The short badge the physical-glyph row shows for its policy.</summary>
    /// <param name="mode">The persisted glyph policy.</param>
    /// <returns>The badge text.</returns>
    internal static string GlyphModeLabel(DeviceGlyphSelection mode)
    {
        return mode switch
        {
            DeviceGlyphSelection.Automatic => "AUTO",
            DeviceGlyphSelection.NativeSteam => "STEAM",
            DeviceGlyphSelection.ManualReviewedProfile => "REVIEWED",
            _ => "AUTO"
        };
    }

    internal static DescriptorRow PhysicalGlyphSelectionView(
        DeviceGlyphSelection mode,
        PhysicalGlyphSelectionResult selection)
    {
        var trailing = GlyphModeLabel(mode);
        string description;
        DescriptorStatus status;
        if (selection.Profile is { } profile)
        {
            description = $"{profile.Manifest.DisplayName} · revision {profile.Manifest.Revision} · "
                          + $"source {profile.Manifest.SourceRevision}"
                          + (selection.FellBackFromMissingManualProfile
                              ? " · selected reviewed profile is missing; Automatic fallback"
                              : string.Empty);
            status = selection.FellBackFromMissingManualProfile
                ? DescriptorStatus.Warning
                : DescriptorStatus.Available;
        }
        else if (selection.FallbackReason is PhysicalGlyphFallbackReason.NativeSteamSelected)
        {
            description = "Steam and generic first-party glyphs remain unchanged.";
            status = DescriptorStatus.Available;
        }
        else
        {
            description = selection.FallbackReason switch
            {
                PhysicalGlyphFallbackReason.DeviceIntegrationDisabled =>
                    "Device integration is off; generic glyphs remain active.",
                PhysicalGlyphFallbackReason.ExactDeviceMismatch =>
                    "The package profile does not match this exact device; generic glyphs remain active.",
                _ => "No reviewed physical profile is available; generic glyphs remain active."
            };
            status = DescriptorStatus.Warning;
        }

        return new DescriptorRow(
            DeviceHostRowIds.GlyphSelection,
            "Physical glyphs",
            description,
            trailing,
            true,
            status);
    }

    internal static string LifecycleLabel(DeviceCycleState state)
    {
        return state switch
        {
            DeviceCycleState.Disabled => "Device integration off",
            DeviceCycleState.Detected => "Device detected",
            DeviceCycleState.Passive => "Device passive",
            DeviceCycleState.Activating => "Device activating",
            DeviceCycleState.Active => "Device active",
            DeviceCycleState.Degraded => "Device partly available",
            DeviceCycleState.Suspended => "Device suspended",
            DeviceCycleState.Deactivating => "Device deactivating",
            DeviceCycleState.Faulted => "Device faulted",
            _ => state.ToString()
        };
    }
}
