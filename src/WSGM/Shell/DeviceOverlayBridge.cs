using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Settings;
using WSGM.Input;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>Stable final-overlay section selected from a semantic capability role.</summary>
/// <remarks>
///     Each section is a page in the Device destination, not a heading in one long list. The split is
///     driven by capability role alone, so a plugin that publishes nothing for a section simply causes
///     that page to be absent — no section is a fixed fixture of the UI.
/// </remarks>
internal enum DeviceOverlaySection
{
    /// <summary>Identity, scenario mode, and anything that describes the device as a whole.</summary>
    Overview,

    /// <summary>Named hardware profiles the user selects between.</summary>
    Profiles,

    /// <summary>Power limits, fans, charge behaviour, and how the device performs.</summary>
    PowerAndThermals,

    /// <summary>
    ///     The physical controller and everything that describes it, glyph artwork included.
    /// </summary>
    /// <remarks>
    ///     Glyphs were a page of their own next to this one, which meant a user looking for controller
    ///     settings had two cards to guess between and neither held all of it. The preview and input
    ///     test still render here; they are a picture of this controller's buttons, not a separate
    ///     subject.
    /// </remarks>
    ControllerAndMotion,

    /// <summary>Device-specific OEM buttons and their assignments.</summary>
    Oem,

    /// <summary>Lighting and any remaining device features.</summary>
    LightingAndFeatures,

    /// <summary>Health, recovery, and anything that exists to be read rather than changed.</summary>
    Diagnostics
}

/// <summary>One presentation-only semantic capability row for the final Device destination.</summary>
internal sealed record DeviceOverlayCapability(
    string CapabilityId,
    string? InstanceId,
    DeviceOverlaySection Section,
    DescriptorStatus Status,
    string Title,
    string Description,
    string TrailingText,
    bool CanInvoke,
    CapabilityValue? CurrentValue = null,
    CapabilityValue? NextValue = null)
{
    /// <summary>The descriptor's semantic role, for consumers pairing related controls.</summary>
    public CapabilityRole Role { get; init; } = CapabilityRole.GenericReadOnly;

    /// <summary>The declared overlay section holding this row, or null for the WSGM fallback home.</summary>
    public string? PluginSectionId { get; init; }

    /// <summary>The declared category within that section, or null for the section's lead group.</summary>
    public string? CategoryId { get; init; }

    /// <summary>Placement within its section and category.</summary>
    public int SortOrder { get; init; }

    /// <summary>
    ///     What kind of value this capability carries, so the overlay picks a control:
    ///     integer range → slider, choice → dropdown, boolean → toggle, text → textbox, color →
    ///     swatch/editor, otherwise a plain action button.
    /// </summary>
    public CapabilityValueKind ValueKind { get; init; } = CapabilityValueKind.None;

    /// <summary>
    ///     Whether the current value may be written. A read-only ranged capability still shows
    ///     its value but renders as a reading, not an adjustable control.
    /// </summary>
    public bool Writable { get; init; }

    /// <summary>Inclusive lower bound of an integer capability, or null when it has no range.</summary>
    public int? Minimum { get; init; }

    /// <summary>Inclusive upper bound of an integer capability, or null when it has no range.</summary>
    public int? Maximum { get; init; }

    /// <summary>Step between legal integer values; at least 1 when a range is present.</summary>
    public int? Step { get; init; }

    /// <summary>Unit for a numeric value, used to format the slider's live label.</summary>
    public CapabilityUnit Unit { get; init; } = CapabilityUnit.None;

    /// <summary>The ordered legal values of a choice capability, empty otherwise.</summary>
    public IReadOnlyList<CapabilityChoice> Choices { get; init; } = [];

    /// <summary>Maximum text length for a text capability, or null when it has none.</summary>
    public int? MaximumLength { get; init; }
}

/// <summary>One category heading of a plugin-declared overlay section.</summary>
internal sealed record DeviceOverlayCategory(string Id, string Title);

/// <summary>One plugin-declared overlay section, projected for presentation.</summary>
/// <remarks>
///     Presentation-only: titles are already resolved from the WSGM-owned key vocabulary or bounded
///     plugin text, so no consumer of this record touches SDK display metadata again.
/// </remarks>
internal sealed record DeviceOverlayPluginSection(
    string SectionId,
    string Title,
    string Description,
    SectionIcon Icon,
    IReadOnlyList<DeviceOverlayCategory> Categories)
{
    /// <summary>The subject this section claims, which is how a WSGM-owned section finds its home.</summary>
    /// <remarks>
    ///     Carried through from the declaration rather than inferred from the title: a plugin may call
    ///     its power page anything, and the key is the only part of the declaration that means the same
    ///     thing to WSGM. <see cref="SettingSectionKey.Custom" /> claims no subject and absorbs nothing.
    /// </remarks>
    public SettingSectionKey Key { get; init; } = SettingSectionKey.Custom;
}

/// <summary>One control in the glyph preview.</summary>
/// <param name="Control">The physical control this glyph stands for.</param>
/// <param name="Label">Human-readable control name.</param>
/// <param name="Plan">The resolved artwork, or a plan that carries none.</param>
internal sealed record DeviceOverlayGlyphPreviewItem(
    GlyphControlId Control,
    string Label,
    PhysicalGlyphRenderPlan Plan);

/// <summary>The glyph preview and its live input test.</summary>
/// <remarks>
///     One projection for both, because they are the same picture answering two questions: whether the
///     plugin's artwork resolves at all, and whether pressing a control reaches WSGM as the control the
///     artwork claims. Separating them would mean drawing the same map twice.
/// </remarks>
/// <param name="ProfileName">The profile supplying the artwork, or why none is.</param>
/// <param name="Detail">One line about the profile's provenance.</param>
/// <param name="Items">Every control the profile maps, in canonical order.</param>
/// <param name="InputTestAvailable">Whether physical input is reaching the surface.</param>
internal sealed record DeviceOverlayGlyphPreview(
    string ProfileName,
    string Detail,
    IReadOnlyList<DeviceOverlayGlyphPreviewItem> Items,
    bool InputTestAvailable);

/// <summary>Complete bounded Device-surface snapshot produced from coordinator-owned state.</summary>
/// <remarks>
///     The direct rows are WSGM's own controls — AutoTDP moves the plugin's power limit rather than
///     being one, the controller target and glyph selection are WSGM settings, the profile rows are
///     stored configuration, and recovery is an action on the cycle itself. Each carries its stable
///     focus key as the <see cref="DescriptorRow.Id" />, and a null row is simply not shown.
/// </remarks>
internal sealed record DeviceOverlaySnapshot(
    bool Visible,
    string Status,
    string Detail,
    DescriptorRow? GlyphSelection,
    IReadOnlyList<DeviceOverlayCapability> Capabilities,
    DescriptorRow? AutoTdp = null,
    DescriptorRow? Controller = null,
    DescriptorRow? Recovery = null,
    DescriptorRow? Profile = null,
    DeviceOverlayGlyphPreview? GlyphPreview = null,
    DescriptorRow? AuthoredProfile = null)
{
    /// <summary>The selected physical button presentation policy.</summary>
    public DeviceGlyphSelection GlyphMode { get; init; }

    /// <summary>Plugin-declared overlay sections in presentation order.</summary>
    public IReadOnlyList<DeviceOverlayPluginSection> PluginSections { get; init; } =
        DeviceOverlayBridge.ProjectSections(DeviceSections.All);
}

/// <summary>Closed semantic source consumed by the Device overlay destination.</summary>
internal interface IDeviceOverlaySource : IDisposable
{
    event Action? Changed;

    /// <summary>Raised for each physical sample while the glyph input test is observing.</summary>
    /// <remarks>
    ///     Separate from <see cref="Changed" /> because it fires at input rate. A consumer must treat it
    ///     as a hint to update one visual state, never as a reason to rebuild a page.
    /// </remarks>
    event Action<CanonicalControllerSample>? PhysicalSampleReceived;

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

    DeviceOverlaySnapshot Snapshot();

    Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default);

    Task SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection selection, CancellationToken cancellationToken = default);

    /// <summary>Turns AutoTDP on or off and persists the choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new setting is persisted.</returns>
    Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves the global default controller target to the next one and persists it.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new target is persisted and applied.</returns>
    Task CycleControllerTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>Retries a faulted device cycle now instead of waiting for the automatic retry.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>A task completing once the attempt has been made.</returns>
    Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves to the next named hardware profile, or to none, and persists it.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new selection is persisted and applied.</returns>
    Task CycleHardwareProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves to the next authored fan profile, or to none, and applies it.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new selection is persisted and applied.</returns>
    /// <remarks>
    ///     Scoped to the running application when there is one, and global otherwise. That is the
    ///     choice a user makes by opening this row mid-game: they are changing the profile for what they
    ///     are playing, and silently changing it for everything would be the wrong reading — while on
    ///     the desktop, with nothing running, there is no per-game scope to mean.
    /// </remarks>
    Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Adapts the authoritative coordinator to the overlay without exposing transport or plugin data.
/// </summary>
internal sealed class DeviceOverlayBridge : IDeviceOverlaySource
{
    private readonly AutoTdpService? _autoTdp;
    private readonly DeviceCoordinator _coordinator;
    private readonly PhysicalGlyphService _glyphs;
    private readonly Lock _sampleGate = new();
    private bool _disposed;
    private int _sampleObservers;

    internal DeviceOverlayBridge(DeviceCoordinator coordinator, AutoTdpService? autoTdp)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
        _autoTdp = autoTdp;
        // One service over the coordinator's catalog, so its bounded geometry cache is shared by
        // every preview and is invalidated by the same catalog change that replaces the profiles.
        _glyphs = new PhysicalGlyphService(coordinator.PhysicalGlyphCatalog);
        _coordinator.StateChanged += OnStateChanged;
        _coordinator.Capabilities.Changed += OnCapabilityViewsChanged;
        _coordinator.ConfigurationChanged += OnConfigurationChanged;
        if (_autoTdp is not null)
        {
            // The AutoTDP row renders live state, not the stored setting, and that state changes
            // with no capability view and no configuration change behind it.
            _autoTdp.StatusChanged += OnAutoTdpStatusChanged;
        }
    }

    public event Action? Changed;

    /// <inheritdoc />
    public Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.CycleAuthoredProfileAsync(cancellationToken);
    }

    public DeviceOverlaySnapshot Snapshot()
    {
        var state = _coordinator.State;
        var package = _coordinator.InstalledPackage;
        var controllerStatus = _coordinator.Controllers.Snapshot();
        var declaredSections = _coordinator.Capabilities.Sections;
        HashSet<string> declaredSectionIds = new(
            DeviceSections.IncludePredefined(declaredSections).Select(section => section.SectionId),
            StringComparer.Ordinal);
        var capabilities = _coordinator.Capabilities.Snapshot()
            .Take(128)
            .Select(view => ToOverlayCapability(view, declaredSectionIds))
            .ToList();
        if (_coordinator.ManualTdpUnified)
        {
            for (var index = 0; index < capabilities.Count; index++)
            {
                capabilities[index] = ProjectManualTdp(capabilities[index], true);
            }
        }

        var glyphSelectionState = _coordinator.PhysicalGlyphSelectionSnapshot();
        var glyphSelection = PhysicalGlyphSelectionView(
            _coordinator.PhysicalGlyphSelection,
            glyphSelectionState);
        var autoTdp = AutoTdpView(
            _coordinator.AutoTdpEnabled,
            _autoTdp?.Status,
            _autoTdp?.Availability);
        var recovery = RecoveryView(state);
        var controller = ControllerView(
            _coordinator.ControllerManagementEnabled,
            controllerStatus);
        var profile = ProfileView(
            _coordinator.HardwareProfileIds,
            _coordinator.SelectedHardwareProfileId);
        var
            authored = _coordinator.AuthoredProfileSelection();
        var glyphPreview = GlyphPreview(
            glyphSelectionState,
            _glyphs,
            // The input test is live only while the plugin's canonical samples are actually
            // reaching WSGM. Offering it otherwise would show a map that can never light up.
            controllerStatus.UiSource is UiInputSource.ManagedCanonical);

        if (package is { Valid: false })
        {
            capabilities.Add(new DeviceOverlayCapability(
                $"wsgm.package.rejected.{package.Manifest?.Id ?? "unknown"}",
                package.Manifest?.Version,
                DeviceOverlaySection.Diagnostics,
                DescriptorStatus.Unsupported,
                package.Manifest?.Id ?? "Invalid device package",
                package.Detail ?? "The installed package did not pass validation.",
                package.RejectionCode ?? "INVALID",
                false));
        }

        var discovery = _coordinator.PackageDiscovery;
        if (discovery.Inventory.Cardinality is DevicePackageCardinality.Multiple)
        {
            capabilities.AddRange(discovery.Inventory.PackageRoots.Take(16)
                .Select(packageRoot => new DeviceOverlayCapability(
                    $"wsgm.package.multiple.{Path.GetFileName(packageRoot)}",
                    null,
                    DeviceOverlaySection.Diagnostics,
                    DescriptorStatus.Unsupported,
                    Path.GetFileName(packageRoot),
                    $"{discovery.Detail} Path: {packageRoot}",
                    discovery.ErrorCode ?? "MULTIPLE",
                    false)));
        }

        // OrderBy is stable, so rows keep their order within a section.
        capabilities = [.. capabilities.OrderBy(capability => capability.Section)];
        var detail = package is null
            ? state is DeviceCycleState.Detected or DeviceCycleState.Passive
                ? "No compatible verified device package is active."
                : "Device integration is waiting for a compatible handheld."
            : $"{package.Manifest?.Id} {package.Manifest?.Version}";
        return new DeviceOverlaySnapshot(
            _coordinator.IntegrationEnabled,
            LifecycleLabel(state),
            detail,
            glyphSelection,
            capabilities,
            autoTdp,
            controller,
            recovery,
            profile,
            glyphPreview,
            authored is { } selection
                ? AuthoredProfileView(
                    selection.Profiles,
                    selection.SelectedProfileId,
                    selection.ApplicationScoped)
                : null)
        {
            GlyphMode = _coordinator.PhysicalGlyphSelection,
            PluginSections = ProjectSections(declaredSections)
        };
    }

    public async Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (!capability.CanInvoke)
        {
            return;
        }

        await _coordinator.ExecuteCapabilityAsync(
            capability.CapabilityId,
            capability.InstanceId,
            capability.NextValue,
            TimeSpan.FromSeconds(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection selection,
        CancellationToken cancellationToken = default)
    {
        return _coordinator.SetPhysicalGlyphSelectionAsync(selection, cancellationToken);
    }

    public Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.ToggleAutoTdpAsync(cancellationToken);
    }

    public Task CycleControllerTargetAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.SetControllerTargetAsync(
            NextTarget(
                _coordinator.Controllers.Snapshot().Target,
                _coordinator.Controllers.SupportedTargets),
            cancellationToken);
    }

    public Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.RetryAfterFaultAsync(cancellationToken);
    }

    public Task CycleHardwareProfileAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.SelectHardwareProfileAsync(
            NextProfile(_coordinator.HardwareProfileIds, _coordinator.SelectedHardwareProfileId),
            cancellationToken);
    }

    /// <inheritdoc />
    public PhysicalGlyphRenderPlan? NavigationHint(GlyphControlId control)
    {
        if (_disposed)
        {
            return null;
        }

        // The NavigationHint surface carries its own authorization: the service refuses it unless
        // the active input source is the managed handheld, which is exactly the condition under
        // which replacing a written letter with a device glyph is correct.
        var plan = _glyphs.Resolve(
            _coordinator.PhysicalGlyphSelectionSnapshot(),
            control,
            PhysicalGlyphSurface.NavigationHint,
            _coordinator.Controllers.Snapshot().UiSource is UiInputSource.ManagedCanonical,
            PhysicalGlyphTheme.Dark,
            1);
        return plan.UsesDeviceArtwork ? plan : null;
    }

    /// <inheritdoc />
    public event Action<CanonicalControllerSample>? PhysicalSampleReceived;

    /// <inheritdoc />
    public IDisposable ObservePhysicalSamples()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sampleGate)
        {
            if (_sampleObservers++ == 0)
            {
                _coordinator.Controllers.PhysicalSampleObserved += OnPhysicalSample;
            }
        }

        return new SampleLease(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.Capabilities.Changed -= OnCapabilityViewsChanged;
        _coordinator.ConfigurationChanged -= OnConfigurationChanged;
        if (_autoTdp is not null)
        {
            _autoTdp.StatusChanged -= OnAutoTdpStatusChanged;
        }

        lock (_sampleGate)
        {
            if (_sampleObservers > 0)
            {
                _coordinator.Controllers.PhysicalSampleObserved -= OnPhysicalSample;
                _sampleObservers = 0;
            }
        }

        // The service subscribed to the catalog's change event, so it has to be released here or it
        // keeps this bridge's geometry cache alive for the rest of the session.
        _glyphs.Dispose();
    }

    internal static DeviceOverlayCapability ProjectManualTdp(DeviceOverlayCapability capability, bool unified)
    {
        return !unified
            ? capability
            : capability.Role switch
            {
                CapabilityRole.PowerSustainedLimit => capability with { Title = "TDP" },
                CapabilityRole.PowerSlowLimit => capability with { Writable = false, CanInvoke = false },
                _ => capability
            };
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
            ControllerManagementState.Active => DescriptorStatus.Available,
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
            "device.controller-target",
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
    private static DeviceOverlayGlyphPreview? GlyphPreview(
        PhysicalGlyphSelectionResult selection,
        PhysicalGlyphService glyphs,
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
                true,
                PhysicalGlyphTheme.Dark,
                1);
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
    /// <param name="applicationScoped">Whether that choice came from an application override.</param>
    /// <returns>The row, or null when the device has no authored profiles at all.</returns>
    /// <remarks>
    ///     Null when nothing has been authored, unlike the hardware-profile row above. That row is
    ///     always present because hardware profiles come from the plugin and a user cannot create one;
    ///     these are created in Settings, and a row offering a choice between nothing would be an
    ///     invitation to press a button that cannot do anything.
    ///     <para>
    ///         The scope is in the description rather than implied, because a profile chosen for one game
    ///         and the same profile chosen for everything read identically otherwise — and the difference is
    ///         what the user changes when they open this row mid-game.
    ///     </para>
    /// </remarks>
    internal static DescriptorRow? AuthoredProfileView(
        IReadOnlyList<DeviceAuthoredProfile> profiles,
        string? selectedProfileId,
        bool applicationScoped)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
        {
            return null;
        }

        var display = profiles[0].CapabilityId switch
        {
            DeviceAuthoredProfileCapabilities.FanCurve => new CapabilityDisplay { Key = DisplayKey.FanCurve },
            DeviceAuthoredProfileCapabilities.Lighting => new CapabilityDisplay { Key = DisplayKey.Lighting },
            _ => new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Device profile" }
        };
        var label = DisplayLabel(display);

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
                "device.authored-profile",
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
            : applicationScoped
                ? $"1 of {profiles.Count} · applies to this game only"
                : $"1 of {profiles.Count} · applies to everything";

        return new DescriptorRow(
            "device.authored-profile",
            label,
            scope,
            selected is null ? "NONE" : selected.Name.ToUpperInvariant(),
            true,
            selected is null ? DescriptorStatus.None : DescriptorStatus.Available);
    }

    /// <summary>Projects named hardware profiles into the Profiles page's own row.</summary>
    /// <param name="profileIds">The profiles this machine's stored values define.</param>
    /// <param name="selected">The profile currently selected, or null for none.</param>
    /// <returns>The row.</returns>
    /// <remarks>
    ///     Always present, unlike the recovery row. Profiles are a feature a user has to find before
    ///     they can use it, so the row says where to author one when none exists yet — an absent row
    ///     would just look like the feature is missing.
    /// </remarks>
    internal static DescriptorRow ProfileView(
        IReadOnlyList<string> profileIds,
        string? selected)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        if (profileIds.Count == 0)
        {
            return new DescriptorRow(
                "device.hardware-profile",
                "Hardware profile",
                "No profiles are defined · add profile values in Settings",
                "NONE",
                false);
        }

        var active = selected is { Length: > 0 } && profileIds.Contains(selected, StringComparer.Ordinal);
        var description = active
            ? $"1 of {profileIds.Count} · overrides power and battery defaults while selected"
            : $"{profileIds.Count} defined · none selected";
        return new DescriptorRow(
            "device.hardware-profile",
            "Hardware profile",
            description,
            // A selection naming a profile that no longer defines anything reads as NONE, which is
            // what it now behaves as: the resolver finds no value under that name and falls through.
            active ? selected!.ToUpperInvariant() : "NONE",
            true,
            active ? DescriptorStatus.Available : DescriptorStatus.None);
    }

    /// <summary>The next profile in the cycle, with none between the last and the first.</summary>
    /// <param name="profileIds">The profiles in presentation order.</param>
    /// <param name="selected">The current selection.</param>
    /// <returns>The next selection, or null for none.</returns>
    /// <remarks>
    ///     None is a position in the cycle rather than a separate control, so a user can always get back
    ///     to unmodified defaults with the same button that got them here.
    /// </remarks>
    internal static string? NextProfile(IReadOnlyList<string> profileIds, string? selected)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        if (profileIds.Count == 0)
        {
            return null;
        }

        var index = selected is null
            ? -1
            : IndexOfOrdinal(profileIds, selected);

        // An unknown selection behaves as none, so cycling from it lands on the first profile
        // rather than doing nothing.
        var next = index + 1;
        return next >= profileIds.Count ? null : profileIds[next];
    }

    private static int IndexOfOrdinal(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
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
            "device.retry",
            "Retry device integration",
            $"{LifecycleLabel(state)} · starts one manual recovery attempt",
            "READY",
            true,
            DescriptorStatus.Warning);
    }

    private static string TargetLabel(ManagedControllerTarget target)
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
        const string autoTdpKey = "device.auto-tdp";
        if (availability is { Available: false })
        {
            return new DescriptorRow(autoTdpKey, "AutoTDP", availability.Detail, "OFF",
                false, DescriptorStatus.Unsupported);
        }

        if (!enabled)
        {
            return new DescriptorRow(
                autoTdpKey,
                "AutoTDP",
                "Move the power limit from measured frame delivery",
                "OFF",
                true);
        }

        var detail = status?.Detail ?? "Starting.";
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

        return new DescriptorRow(autoTdpKey, "AutoTDP", detail, trailing, true, health);
    }

    private void ReleasePhysicalSamples()
    {
        lock (_sampleGate)
        {
            if (_sampleObservers == 0 || --_sampleObservers > 0)
            {
                return;
            }

            _coordinator.Controllers.PhysicalSampleObserved -= OnPhysicalSample;
        }
    }

    private void OnPhysicalSample(CanonicalControllerSample sample)
    {
        PhysicalSampleReceived?.Invoke(sample);
    }

    /// <summary>The next target in the cycle order.</summary>
    /// <param name="current">The target in effect, or null when none is.</param>
    /// <param name="supported">
    ///     Targets the backend can build. An empty list means nothing has been discovered yet and the
    ///     full order is used, which is also the order the tests pin.
    /// </param>
    /// <returns>The target the row moves to.</returns>
    /// <remarks>
    ///     Steam Deck first from nothing, because it is the target that carries every control the
    ///     canonical model defines; the other two exist for compatibility with software that does not
    ///     understand it. Unsupported targets are skipped rather than offered: selecting one persists a
    ///     target the backend then refuses to create, which leaves controller management unavailable.
    /// </remarks>
    internal static ManagedControllerTarget NextTarget(
        ManagedControllerTarget? current,
        IReadOnlyList<ManagedControllerTarget>? supported = null)
    {
        ManagedControllerTarget[] order =
        [
            ManagedControllerTarget.SteamDeckComposite,
            ManagedControllerTarget.Xbox360,
            ManagedControllerTarget.DualShock4
        ];
        var offered = supported is { Count: > 0 }
            ? [.. order.Where(supported.Contains)]
            : order;
        if (offered.Length == 0)
        {
            return current ?? ManagedControllerTarget.SteamDeckComposite;
        }

        var index = current is { } target ? Array.IndexOf(offered, target) : -1;
        return offered[(index + 1) % offered.Length];
    }

    private void OnStateChanged(DeviceCycleState _)
    {
        Changed?.Invoke();
    }

    private void OnCapabilityViewsChanged(IReadOnlyList<DeviceCapabilityView> _)
    {
        Changed?.Invoke();
    }

    private void OnConfigurationChanged()
    {
        Changed?.Invoke();
    }

    // Raised from AutoTDP's own tick loop; the overlay consumer is UI-owned, so marshal first.
    private void OnAutoTdpStatusChanged(AutoTdpStatus _)
    {
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    internal static DeviceOverlayCapability ToOverlayCapability(
        DeviceCapabilityView view,
        IReadOnlySet<string> declaredSections)
    {
        var descriptor = view.Descriptor;
        var projection = view.Projection;
        var state = projection.State;
        var displayed = projection.PendingValue
                        ?? state.ObservedValue
                        ?? projection.DesiredValue;
        var actionOnlyReady = state is { Available: true, Reason: null, Quality: HardwareStateQuality.Unknown }
                              && descriptor is { SupportsAction: true, SupportsRead: false };
        var current = actionOnlyReady
                      || state is
                      {
                          Available: true,
                          Quality: HardwareStateQuality.Observed or HardwareStateQuality.Verified
                      };
        var next = NextValue(descriptor, displayed);
        var colorWrite = descriptor.ValueKind is CapabilityValueKind.Color
                         && displayed?.ColorValue is not null;
        var canInvoke = current
                        && (descriptor.SupportsAction
                            || (descriptor.SupportsWrite && (next is not null || colorWrite)));
        var description = projection.Progress switch
        {
            CommandProgress.Pending => "Applying requested value…",
            CommandProgress.Uncertain => "Last request is unverified — refresh before retrying",
            CommandProgress.Failed => view.LastResult?.Reason?.Detail ?? "Last request failed",
            _ when projection.DesiredValueOutOfRange =>
                "Saved value is outside the current firmware range",
            _ when state.Reason is not null => state.Reason.Detail,
            _ when actionOnlyReady => "Ready · action has no readback",
            _ => $"{QualityLabel(state.Quality)} · {PersistenceLabel(descriptor.Persistence)}"
        } ?? "Capability state is unavailable.";
        return new DeviceOverlayCapability(
            descriptor.CapabilityId,
            descriptor.InstanceId,
            SectionFor(descriptor.Role),
            StatusFor(projection),
            CapabilityDisplayLabels.For(descriptor.Display, "Device control"),
            description,
            descriptor.SupportsAction ? "RUN" : FormatValue(displayed, descriptor.Unit),
            canInvoke,
            displayed,
            descriptor.SupportsAction ? null : next)
        {
            Role = descriptor.Role,
            PluginSectionId = descriptor.SectionId is { } sectionId
                              && declaredSections.Contains(sectionId)
                ? sectionId
                : null,
            CategoryId = descriptor.SectionId is { } declared
                         && declaredSections.Contains(declared)
                ? descriptor.CategoryId
                : null,
            SortOrder = descriptor.SortOrder,
            ValueKind = descriptor.ValueKind,
            Writable = descriptor.SupportsWrite,
            Minimum = descriptor.Minimum,
            Maximum = descriptor.Maximum,
            Step = descriptor.Step,
            Unit = descriptor.Unit,
            Choices = descriptor.Choices,
            MaximumLength = descriptor.MaximumLength
        };
    }

    internal static DescriptorRow PhysicalGlyphSelectionView(
        DeviceGlyphSelection mode,
        PhysicalGlyphSelectionResult selection)
    {
        var trailing = mode switch
        {
            DeviceGlyphSelection.Automatic => "AUTO",
            DeviceGlyphSelection.NativeSteam => "STEAM",
            DeviceGlyphSelection.ManualReviewedProfile => "REVIEWED",
            _ => "AUTO"
        };
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
            "device.glyph-selection",
            "Physical glyphs",
            description,
            trailing,
            true,
            status);
    }

    /// <summary>Projects the declared overlay sections for presentation, in declared order.</summary>
    internal static IReadOnlyList<DeviceOverlayPluginSection> ProjectSections(
        IReadOnlyList<CapabilitySection> sections)
    {
        return
        [
            .. DeviceSections.IncludePredefined(sections)
                .Select((section, index) => (Section: section, Index: index))
                .OrderBy(item => item.Section.SortOrder)
                .ThenBy(item => item.Index)
                .Select(item => new DeviceOverlayPluginSection(
                    item.Section.SectionId,
                    item.Section.SectionId switch
                    {
                        DeviceSections.RgbId => "RGB",
                        DeviceSections.InfoId => "Info",
                        _ => SectionTitle(item.Section.Key, item.Section.CustomTitle)
                    },
                    item.Section.CustomDescription ?? SectionDescription(item.Section.Key),
                    item.Section.Icon,
                    [
                        .. item.Section.Categories
                            .Select((category, categoryIndex) => (Category: category, Index: categoryIndex))
                            .OrderBy(entry => entry.Category.SortOrder)
                            .ThenBy(entry => entry.Index)
                            .Select(entry => new DeviceOverlayCategory(
                                entry.Category.CategoryId,
                                SectionTitle(entry.Category.Key, entry.Category.CustomTitle)))
                    ])
                {
                    Key = item.Section.Key
                })
        ];
    }

    /// <summary>The WSGM-owned title behind a section key; custom text is bounded plugin text.</summary>
    private static string SectionTitle(SettingSectionKey key, string? custom)
    {
        return key switch
        {
            SettingSectionKey.Custom => custom ?? "Device",
            SettingSectionKey.General => "General",
            SettingSectionKey.Power => "Power",
            SettingSectionKey.Fans => "Fans",
            SettingSectionKey.Lighting => "Lighting",
            SettingSectionKey.Controller => "Controller",
            SettingSectionKey.Display => "Display",
            SettingSectionKey.Advanced => "Advanced",
            SettingSectionKey.Diagnostics => "Diagnostics",
            _ => key.ToString()
        };
    }

    /// <summary>WSGM's own card description for a keyed section that supplies none.</summary>
    private static string SectionDescription(SettingSectionKey key)
    {
        return key switch
        {
            SettingSectionKey.Power => "Power and performance controls",
            SettingSectionKey.Fans => "Cooling control and readings",
            SettingSectionKey.Lighting => "Device lighting",
            SettingSectionKey.Controller => "Controller, motion, and rumble",
            SettingSectionKey.Display => "Display features",
            SettingSectionKey.Diagnostics => "Health and readings",
            _ => "Device controls"
        };
    }

    private static DeviceOverlaySection SectionFor(CapabilityRole role)
    {
        return role switch
        {
            CapabilityRole.ScenarioMode => DeviceOverlaySection.Overview,
            CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit
                or CapabilityRole.PowerFastLimit or CapabilityRole.PowerPeakLimit
                or CapabilityRole.FanMode or CapabilityRole.FanDuty
                or CapabilityRole.FanTargetRpm or CapabilityRole.FanCurve
                or CapabilityRole.FanMeasuredRpm or CapabilityRole.ChargeLimit
                or CapabilityRole.ChargeProtectionMode or CapabilityRole.ChargeBypass
                or CapabilityRole.Telemetry
                // Variable refresh sits with the frame limit and power controls it interacts with,
                // not among the lighting oddments, because that is where a user goes to change how the
                // device performs.
                or CapabilityRole.VariableRefreshRate => DeviceOverlaySection.PowerAndThermals,
            CapabilityRole.ControllerSource or CapabilityRole.MotionSource
                or CapabilityRole.HapticSink => DeviceOverlaySection.ControllerAndMotion,
            CapabilityRole.OemControl => DeviceOverlaySection.Oem,
            CapabilityRole.LightingPower or CapabilityRole.LightingBrightness
                or CapabilityRole.LightingZoneColor or CapabilityRole.LightingEffect
                or CapabilityRole.LightingEffectSpeed
                or CapabilityRole.GenericToggle or CapabilityRole.GenericRange
                or CapabilityRole.GenericChoice or CapabilityRole.GenericAction
                or CapabilityRole.GenericText
                => DeviceOverlaySection.LightingAndFeatures,
            // A read-only value is something to consult, not to set, so it belongs with the rest of the
            // diagnostics rather than among the controls a user came to change.
            CapabilityRole.GenericReadOnly => DeviceOverlaySection.Diagnostics,
            _ => DeviceOverlaySection.Overview
        };
    }

    private static DescriptorStatus StatusFor(CapabilityProjection projection)
    {
        if (projection.Progress is CommandProgress.Pending)
        {
            return DescriptorStatus.Progress;
        }

        if (projection.Progress is CommandProgress.Failed
            || projection.State.Quality is HardwareStateQuality.Faulted
            || projection.State.Reason?.Code is CapabilityReasonCode.TransportFaulted)
        {
            return DescriptorStatus.Faulted;
        }

        if (projection.Progress is CommandProgress.Uncertain || projection.DesiredValueOutOfRange)
        {
            return DescriptorStatus.Warning;
        }

        if (projection.State.Quality is HardwareStateQuality.Stale
            || projection.State.Reason?.Code is CapabilityReasonCode.GenerationChanged
                or CapabilityReasonCode.ObservationExpired)
        {
            return DescriptorStatus.Stale;
        }

        return projection.State.Reason?.Code switch
        {
            CapabilityReasonCode.ResourceConflict or CapabilityReasonCode.ResourceReleased =>
                DescriptorStatus.ExternallyOwned,
            CapabilityReasonCode.Unsupported
                or CapabilityReasonCode.FirmwareNotVerified
                or CapabilityReasonCode.PrerequisiteMissing => DescriptorStatus.Unsupported,
            _ => projection.State.Available
                ? DescriptorStatus.Available
                : DescriptorStatus.Warning
        };
    }

    private static CapabilityValue? NextValue(
        CapabilityDescriptor descriptor,
        CapabilityValue? current)
    {
        return descriptor.ValueKind switch
        {
            CapabilityValueKind.Boolean => new CapabilityValue
            {
                Kind = CapabilityValueKind.Boolean,
                BooleanValue = !(current?.BooleanValue ?? false)
            },
            CapabilityValueKind.Integer when descriptor is
            {
                Minimum: { } minimum,
                Maximum: { } maximum,
                Step: { } step and > 0
            } => new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = current?.IntegerValue is { } value && value + step <= maximum
                    ? value + step
                    : minimum
            },
            CapabilityValueKind.Choice when descriptor.Choices.Count > 0 => new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = NextChoice(descriptor, current?.ChoiceValue)
            },
            _ => null
        };
    }

    private static string NextChoice(CapabilityDescriptor descriptor, string? current)
    {
        var index = descriptor.Choices.ToList().FindIndex(choice => string.Equals(
            choice.Value,
            current,
            StringComparison.Ordinal));
        return descriptor.Choices[(index + 1) % descriptor.Choices.Count].Value;
    }

    private static string FormatValue(CapabilityValue? value, CapabilityUnit unit)
    {
        if (value is null)
        {
            return "—";
        }

        return value.Kind switch
        {
            CapabilityValueKind.Boolean => value.BooleanValue is true ? "ON" : "OFF",
            CapabilityValueKind.Integer => value.IntegerValue is { } integer
                ? $"{integer.ToString(CultureInfo.CurrentCulture)}{UnitSuffix(unit)}"
                : "—",
            CapabilityValueKind.Choice => value.ChoiceValue ?? "—",
            CapabilityValueKind.Color => value.ColorValue is { } color
                ? $"#{color:X6}"
                : "—",
            CapabilityValueKind.Curve => value.CurveValue.Count > 0
                ? $"{value.CurveValue.Count} points"
                : "—",
            _ => "RUN"
        };
    }

    private static string UnitSuffix(CapabilityUnit unit)
    {
        return unit switch
        {
            CapabilityUnit.Watt => " W",
            CapabilityUnit.Percent => "%",
            CapabilityUnit.Celsius => " °C",
            CapabilityUnit.Rpm => " RPM",
            CapabilityUnit.Milliampere => " mA",
            CapabilityUnit.Millivolt => " mV",
            CapabilityUnit.Megahertz => " MHz",
            CapabilityUnit.Millisecond => " ms",
            _ => string.Empty
        };
    }

    private static string LifecycleLabel(DeviceCycleState state)
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

    private static string QualityLabel(HardwareStateQuality quality)
    {
        return quality switch
        {
            HardwareStateQuality.Verified => "Verified readback",
            HardwareStateQuality.Observed => "Observed",
            HardwareStateQuality.Stale => "Stale",
            HardwareStateQuality.Faulted => "Faulted",
            _ => "Unknown"
        };
    }

    private static string PersistenceLabel(CapabilityPersistence persistence)
    {
        return persistence switch
        {
            CapabilityPersistence.Volatile => "resets on device power loss",
            CapabilityPersistence.DevicePersistent => "stored on device",
            _ => "persistence unknown"
        };
    }

    /// <summary>One observer's claim on the physical sample stream.</summary>
    /// <remarks>
    ///     Idempotent, because a surface torn down twice — closed and then disposed — must not push the
    ///     count below zero and detach a subscription another surface still holds.
    /// </remarks>
    private sealed class SampleLease(DeviceOverlayBridge owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            owner.ReleasePhysicalSamples();
        }
    }
}
