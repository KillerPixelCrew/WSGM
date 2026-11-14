using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Shell;

/// <summary>
///     The one row producer for Steam's native Settings pages and the host rows of its Quick Access tabs.
/// </summary>
/// <remarks>
///     Each feature (display, power, audio, device and controller) builds its rows and their write closures
///     once per read, and places each row on a Settings page section, a Quick Access section, or both. Both
///     surfaces answer through <see cref="SetAsync" /> against the same offered commands, so a row's
///     validation and its write are the same wherever it is drawn. No polling, hardware lifetime or
///     configuration belongs to this projection.
/// </remarks>
/// <param name="brightness">Borrowed live panel-brightness adapter.</param>
/// <param name="resolution">Borrowed display-mode owner, or null when this session must not move the display.</param>
/// <param name="performance">Borrowed RTSS, frame-cap and VRR adapter.</param>
/// <param name="powerProfiles">Borrowed Windows power-profile workflow, shared with the overlay.</param>
/// <param name="cpuBoost">Borrowed CPU boost owner, or null to omit the row.</param>
/// <param name="hybridCores">Borrowed processor core-placement adapter.</param>
/// <param name="audioFormat">Borrowed endpoint-format adapter, or null to omit advanced audio controls.</param>
/// <param name="graphics">Borrowed graphics settings projection, or null when no graphics owner is available.</param>
/// <param name="coordinator">Borrowed device capability and rumble owner, or null for an inactive device integration.</param>
/// <param name="profiles">Borrowed owner of the active application, editing scope and profile operations.</param>
/// <param name="displayTimeouts">Borrowed display-timeout owner, or null to omit timeout controls.</param>
/// <param name="scaledResolutions">Borrowed GPU-scaled resolution owner, or null to omit the source-mode editor.</param>
/// <param name="powerButton">The power button's short-press choice shared with the overlay, or null.</param>
/// <param name="extendedVrr">The extended VRR range shared with the overlay, or null without one.</param>
/// <param name="driverUpdates">The shared GPU driver-update workflow, or null without a live session.</param>
internal sealed partial class SteamNativeSettingsService(
    NativeQamBrightnessService brightness,
    DisplayResolutionService? resolution,
    PerformanceServiceNativeQamAdapter performance,
    NativeQamPowerProfileService powerProfiles,
    CpuBoostService? cpuBoost,
    NativeQamHybridCoreService hybridCores,
    NativeQamAudioFormatService? audioFormat,
    SteamGraphicsService? graphics,
    DeviceCoordinator? coordinator,
    ProfileService profiles,
    DisplayTimeouts? displayTimeouts = null,
    ScaledResolutionService? scaledResolutions = null,
    PowerButtonSetting? powerButton = null,
    ExtendedVrrService? extendedVrr = null,
    IGpuDriverUpdateSource? driverUpdates = null) : ISteamNativeSettingsBackend, ISteamQuickAccessRowsBackend
{
    /// <summary>How long one build answers both surfaces when no change was queued in between.</summary>
    private static readonly TimeSpan SharedBuildLifetime = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _reads = new(1, 1);
    private long _buildEpoch;
    private (List<SteamNativeSettingsPage> Pages, List<SteamQuickAccessRowSection> QuickAccess,
        Dictionary<string, Offered> Commands)? _shared;
    private long _sharedEpoch = -1;
    private long _sharedAt;
    private IReadOnlyDictionary<string, Offered> _offered = new Dictionary<string, Offered>();
    private readonly HeldPublication<List<SteamNativeSettingsPage>> _pages = new();
    private readonly HeldPublication<List<SteamQuickAccessRowSection>> _quickAccess = new();

    /// <summary>Validates one change against the row last offered under its key, then runs its write.</summary>
    /// <param name="key">The row key either surface published.</param>
    /// <param name="value">The renderer's value.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The write's outcome, or why the key or value is no longer offered.</returns>
    public Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (key == "rumble.stop" && value.ValueKind == JsonValueKind.True)
        {
            return StopRumblePreviewAsync();
        }

        if (!Volatile.Read(ref _offered).TryGetValue(key, out var offered)
            || !ValidValue(offered.Row, value))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That setting or value is no longer available."));
        }

        if (offered.Active is { } active && (profiles.Current.Active != active
                                             || profiles.Current.EditsGame != offered.EditsGame))
        {
            return Task.FromResult(new SteamUiCommandResult(false,
                "The running application or editing scope changed."));
        }

        return offered.Write(value, cancellationToken);
    }

    /// <summary>Stops the shared device rumble preview when its owner exists.</summary>
    /// <returns>A successful command after the stop completes; absence is a successful no-op, and owner failures propagate.</returns>
    internal async Task<SteamUiCommandResult> StopRumblePreviewAsync()
    {
        if (coordinator is not null)
        {
            await coordinator.StopRumblePreviewAsync().ConfigureAwait(false);
        }

        return SteamUiCommandResult.Applied;
    }

    /// <summary>Keeps the revision while the pages are unchanged, so an idle publication round re-sends nothing.</summary>
    /// <param name="pages">The pages just built.</param>
    /// <param name="published">The serialized pages of the current revision; replaced on a change.</param>
    /// <param name="revision">The current revision.</param>
    /// <returns>The same revision for identical pages, otherwise the next one.</returns>
    /// <remarks>
    ///     The state is rebuilt on every two-second round. A revision that changed on every build made each
    ///     envelope look new, so the whole Display, Power, Audio and Controller state was evaluated into
    ///     Steam's context every two seconds even when nothing in it had changed.
    /// </remarks>
    /// <typeparam name="T">The published content's type.</typeparam>
    internal static long NextRevision<T>(T pages, ref string? published, long revision)
    {
        var content = JsonSerializer.Serialize(pages);
        if (string.Equals(content, published, StringComparison.Ordinal))
        {
            return revision;
        }

        published = content;
        return revision + 1;
    }

    /// <summary>
    ///     One surface's published content and revision, holding back a change that is only read-only note
    ///     text for up to <see cref="NoteHold" />.
    /// </summary>
    /// <remarks>
    ///     A note row carries live measurements such as fan speed or temperature, which move on every
    ///     two-second telemetry read. Each move re-sent the whole Settings state (about 78 KB on the Atlas)
    ///     and the Quick Access rows (about 54 KB) into Steam's context. A change anywhere else publishes at
    ///     once and carries the latest notes with it.
    /// </remarks>
    /// <typeparam name="T">The published content's type.</typeparam>
    internal sealed class HeldPublication<T> where T : class
    {
        /// <summary>How long a note-only change waits before it is published.</summary>
        internal static readonly TimeSpan NoteHold = TimeSpan.FromSeconds(10);

        private readonly Func<long> _now;
        private string? _content;
        private long _publishedAt;
        private string? _shape;
        private T? _value;

        internal HeldPublication(Func<long>? now = null)
        {
            _now = now ?? Stopwatch.GetTimestamp;
        }

        internal long Revision { get; private set; }

        /// <summary>The content to publish for a fresh build, and its revision.</summary>
        /// <param name="built">The content just built.</param>
        /// <returns>The built content, or the held one while only its notes changed recently.</returns>
        internal (T Value, long Revision) Next(T built)
        {
            var content = JsonSerializer.Serialize(built);
            if (string.Equals(content, _content, StringComparison.Ordinal))
            {
                return (built, Revision);
            }

            var shape = Shape(content);
            var now = _now();
            if (_value is { } held && string.Equals(shape, _shape, StringComparison.Ordinal)
                                   && Stopwatch.GetElapsedTime(_publishedAt, now) < NoteHold)
            {
                return (held, Revision);
            }

            _content = content;
            _shape = shape;
            _value = built;
            _publishedAt = now;
            Revision++;
            return (built, Revision);
        }

        /// <summary>The content with every note row's text removed.</summary>
        private static string Shape(string content)
        {
            var root = JsonNode.Parse(content);
            Strip(root);
            return root?.ToJsonString() ?? string.Empty;
        }

        private static void Strip(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject item:
                    if ((item["Kind"] ?? item["kind"]) is JsonValue kind && kind.TryGetValue(out string? text)
                                                                         && text == SteamSettingsRowKind.Note)
                    {
                        item[item.ContainsKey("Text") ? "Text" : "text"] = null;
                    }

                    foreach (var property in item)
                    {
                        Strip(property.Value);
                    }

                    break;
                case JsonArray array:
                    foreach (var element in array)
                    {
                        Strip(element);
                    }

                    break;
            }
        }
    }

    /// <summary>Reads the Settings pages: one fresh build of every feature's rows.</summary>
    /// <returns>
    ///     Fresh native settings pages and revision; the completed read atomically replaces the exact offered command
    ///     set, which covers the Quick Access rows of the same build too.
    /// </returns>
    /// <remarks>
    ///     Concurrent reads are serialized. Dependencies remain borrowed, and row keys retain the profile/device
    ///     generation they describe.
    /// </remarks>
    internal async ValueTask<SteamNativeSettingsState?> ReadAsync()
    {
        await _reads.WaitAsync().ConfigureAwait(false);
        try
        {
            var built = await SharedBuildAsync().ConfigureAwait(false);
            Volatile.Write(ref _offered, built.Commands);
            var (pages, revision) = _pages.Next(built.Pages);
            LogDisplayState(pages);
            return new SteamNativeSettingsState(pages, revision);
        }
        finally
        {
            _reads.Release();
        }
    }

    /// <summary>Reads the Quick Access rows: one fresh build of every feature's rows.</summary>
    /// <returns>
    ///     The sections the component host places by the layout, with their own revision; the read replaces the
    ///     offered command set like <see cref="ReadAsync" />.
    /// </returns>
    internal async ValueTask<SteamQuickAccessRowsState?> ReadQuickAccessAsync()
    {
        await _reads.WaitAsync().ConfigureAwait(false);
        try
        {
            var built = await SharedBuildAsync().ConfigureAwait(false);
            Volatile.Write(ref _offered, built.Commands);
            var (sections, revision) = _quickAccess.Next(built.QuickAccess);
            return new SteamQuickAccessRowsState(sections, revision);
        }
        finally
        {
            _reads.Release();
        }
    }

    /// <summary>Says a state change was queued, so the next read builds afresh. Any thread.</summary>
    internal void Invalidate()
    {
        Interlocked.Increment(ref _buildEpoch);
    }

    /// <summary>
    ///     One build for both surfaces of a publication round: the Settings pages and the Quick Access
    ///     rows each read every owner, which did the whole build twice every round. Called under
    ///     <see cref="_reads" />.
    /// </summary>
    private async Task<(List<SteamNativeSettingsPage> Pages, List<SteamQuickAccessRowSection> QuickAccess,
        Dictionary<string, Offered> Commands)> SharedBuildAsync()
    {
        var epoch = Volatile.Read(ref _buildEpoch);
        if (_shared is { } shared && _sharedEpoch == epoch
                                  && Stopwatch.GetElapsedTime(_sharedAt) < SharedBuildLifetime)
        {
            return shared;
        }

        var built = await BuildAsync().ConfigureAwait(false);
        _shared = built;
        _sharedEpoch = epoch;
        _sharedAt = Stopwatch.GetTimestamp();
        return built;
    }

    /// <summary>Builds both surfaces from one read of every owner.</summary>
    /// <returns>The Settings pages, the Quick Access sections and every row's offered command.</returns>
    private async Task<(List<SteamNativeSettingsPage> Pages, List<SteamQuickAccessRowSection> QuickAccess,
        Dictionary<string, Offered> Commands)> BuildAsync()
    {
        var offer = new OfferedRows(profiles.Current);
        // The backends are independent, so their reads run together and each page awaits its own.
        // A default read completes with nothing, which omits the rows of an owner that does not exist.
        var lightRead = brightness.ReadAsync();
        var presetsRead =
            PowerPresetRows.ReadAsync(coordinator?.Power.PowerPresets, coordinator?.Power.PowerAssignments);
        var schemesRead = powerProfiles.ReadAsync();
        var boostRead = cpuBoost is null ? default : ReadCpuBoostAsync(cpuBoost, profiles);
        var coresRead = hybridCores.ReadAsync();
        var endpointRead = audioFormat is null ? default : audioFormat.ReadEndpointAsync();
        List<SteamSettingsSection> display = [];
        List<SteamSettingsSection> power = [];
        List<SteamSettingsSection> audio = [];
        List<SteamSettingsSection> controller = [];
        await AddDisplayPageAsync(display, offer, lightRead).ConfigureAwait(false);
        await AddPowerPageAsync(power, offer, presetsRead, schemesRead, boostRead, coresRead)
            .ConfigureAwait(false);
        DeviceSections(power, controller, offer);
        AddDeviceQuickAccess(offer);
        await AddAudioPageAsync(audio, offer, endpointRead).ConfigureAwait(false);
        AddControllerTarget(controller, offer);
        AddRumbleSection(controller, offer);

        AddProfileSection(display, "display", offer);
        AddProfileSection(power, "power", offer);
        AddProfileSection(controller, "controller", offer);
        List<SteamNativeSettingsPage> pages = [];
        // WSGM's Display controls lead the page; Steam's own Big Picture rows follow, then the reset.
        Page("display", display, display.Count > 0 && display[^1].Id == "display.reset" ? display.Count - 1 : display.Count);
        Page("power", power);
        Page("audio", audio);
        Page("controller", controller);
        return (pages, offer.QuickAccess, offer.Commands);

        void Page(string id, List<SteamSettingsSection> sections, int lead = 0)
        {
            if (sections.Count > 0)
            {
                pages.Add(new SteamNativeSettingsPage(id, sections, lead));
            }
        }
    }

    /// <summary>Opens a page with its editing-scope rows and closes it with its reset action.</summary>
    /// <param name="sections">The page's sections; the profile section is inserted first.</param>
    /// <param name="page">The page id the rows are keyed under.</param>
    /// <param name="offer">The read's offered command set.</param>
    private void AddProfileSection(List<SteamSettingsSection> sections, string page, OfferedRows offer)
    {
        if (sections.Count == 0)
        {
            return;
        }

        List<SteamSettingsRow> rows =
        [
            new(page + ".scope", SteamSettingsRowKind.Scope, "Editing", offer.ScopeDetail, Text: offer.ScopeName,
                Accent: offer.Profile.EditsGame)
        ];
        if (offer.Profile.Active.HasApplication)
        {
            offer.Add(rows, new SteamSettingsRow(page + ".game-profile", SteamSettingsRowKind.Boolean,
                    "Use a profile for the running game",
                    "Shares Steam Quick Access's per-game profile switch.",
                    offer.Profile.EditsGame),
                async (value, token) => new SteamUiCommandResult(
                    await profiles.SetGameEnabledAsync(value.GetBoolean(), offer.Profile.Active.ApplicationId, token)
                        .ConfigureAwait(false), null), true);
        }

        sections.Insert(0, new SteamSettingsSection(null, rows, page + ".profile"));
        if (page == "controller" && !offer.Profile.EditsGame)
        {
            return;
        }

        List<SteamSettingsRow> reset = [];
        offer.Add(reset, new SteamSettingsRow(page + ".reset", SteamSettingsRowKind.Action,
                offer.Profile.EditsGame ? "Use global values" : "Reset performance defaults",
                offer.Profile.EditsGame
                    ? "Clears this game's overrides on every surface."
                    : "Clears Global performance preferences.",
                ButtonLabel: "Reset to Default"),
            async (_, token) =>
            {
                await profiles.ResetAsync(token).ConfigureAwait(false);
                return SteamUiCommandResult.Applied;
            }, true);
        sections.Add(new SteamSettingsSection(null, reset, page + ".reset"));
    }

    private void DeviceSections(List<SteamSettingsSection> power, List<SteamSettingsSection> controller,
        OfferedRows offer)
    {
        var profile = offer.Profile;
        if (coordinator is not { IntegrationEnabled: true })
        {
            return;
        }

        if (coordinator.Lighting.Selection() is { } lightingSelection)
        {
            var descriptor = DeviceHostRows.LightingProfileView(lightingSelection.Profiles,
                lightingSelection.Selected,
                coordinator.Lighting.Detail, coordinator.Lighting.Status);
            var choices = DeviceHostRows.ProfileChoices(lightingSelection.Profiles,
                    DeviceHostRows.LightingProfileEmptyLabel(profile.EditsGame))
                .Select(choice => new SteamSettingsChoice(choice.Value,
                    CapabilityDisplayLabels.For(choice.Display, choice.Value)))
                .ToArray();
            var row = new SteamSettingsRow(DeviceHostRowIds.LightingProfile, SteamSettingsRowKind.Choice,
                descriptor.Title, descriptor.Description,
                Text: lightingSelection.Selected.Value ?? string.Empty, Choices: choices,
                Disabled: !descriptor.CanInvoke,
                Accent: lightingSelection.Selected.IsGameOverride);
            List<SteamSettingsRow> lightingProfile = [];
            offer.Add(lightingProfile, row, async (value, token) =>
            {
                await coordinator.Lighting.SelectAsync(EmptyToNull(value.GetString()), token)
                    .ConfigureAwait(false);
                return coordinator.Lighting.Status == DescriptorStatus.Warning
                    ? new SteamUiCommandResult(false, coordinator.Lighting.Detail)
                    : SteamUiCommandResult.Applied;
            }, true);
            Section(controller, "Lighting", "device/lighting-profile", lightingProfile);
        }

        var declarations = CapabilityRowProjection.ProjectSections(coordinator.Capabilities.Sections);
        HashSet<string> declared = new(declarations.Select(section => section.SectionId), StringComparer.Ordinal);
        var capabilities = coordinator.PresentedCapabilities()
            .Select(view => CapabilityRowProjection.ToDeviceRow(view, declared, coordinator.FanPresets,
                profile.Layers))
            .Where(capability => capability.Role is not CapabilityRole.PowerSustainedLimit
                and not CapabilityRole.PowerSlowLimit and not CapabilityRole.VariableRefreshRate)
            .ToArray();
        foreach (var group in capabilities.GroupBy(capability => capability.PluginSectionId))
        {
            var declaration = declarations.FirstOrDefault(section => section.SectionId == group.Key);
            foreach (var category in group.GroupBy(capability => capability.CategoryId))
            {
                var categoryName = declaration?.Categories.FirstOrDefault(item => item.Id == category.Key)?.Title;
                List<SteamSettingsRow> powerRows = [];
                List<SteamSettingsRow> lightingRows = [];
                foreach (var capability in category.OrderBy(capability => capability.SortOrder))
                {
                    var lighting = capability.Role is CapabilityRole.LightingPower or CapabilityRole.LightingBrightness
                                       or CapabilityRole.LightingZoneColor or CapabilityRole.LightingEffect
                                       or CapabilityRole.LightingEffectSpeed
                                   || declaration?.Key is SettingSectionKey.Lighting;
                    var devicePower = CapabilityRowProjection.DevicePageFor(capability.Role)
                                      == Device.Sdk.Capabilities.DeviceSections.PowerId
                                      || declaration?.Key is SettingSectionKey.Power;
                    if (!lighting && !devicePower)
                    {
                        continue;
                    }

                    var captured = capability;
                    offer.Add(lighting ? lightingRows : powerRows, DeviceRow(capability),
                        (value, token) => WriteDeviceAsync(captured, value, token), true);
                }

                var title = declaration?.Title ?? "Device";
                if (categoryName is not null)
                {
                    title += ": " + categoryName;
                }

                var id = "device/" + group.Key + "/" + category.Key;
                Section(power, title, id, powerRows);
                Section(controller, title == "Device" ? "Controller lighting" : title, id, lightingRows);
            }
        }
    }

    /// <summary>Builds a native row bound to the capability's device and descriptor generations.</summary>
    /// <param name="capability">Projected descriptor, current value, write admission and profile override metadata.</param>
    /// <returns>The shared Steam capability row, keyed by capability and instance, with the override accent.</returns>
    internal static SteamSettingsRow DeviceRow(DeviceOverlayCapability capability)
    {
        var key = "device/" + capability.CapabilityId +
                  (capability.InstanceId is { } instance ? "#" + instance : string.Empty);
        // This page's colour editor stages its pick until Save, so the row says so.
        return SteamCapabilityRows.Row(capability is { Writable: true, ValueKind: CapabilityValueKind.Color }
            ? capability with
            {
                Description = "Choose a color, then Save to write the device. " + capability.Description
            }
            : capability, key);
    }

    private async Task<SteamUiCommandResult> WriteDeviceAsync(DeviceOverlayCapability seen, JsonElement value,
        CancellationToken cancellationToken)
    {
        if (coordinator is not { IntegrationEnabled: true }
            || coordinator.Capabilities.TryGetView(new DeviceCapabilityKey(seen.CapabilityId, seen.InstanceId))
                is not { } current)
        {
            return new SteamUiCommandResult(false, "The device capability changed while you were choosing.");
        }

        if (!SteamCapabilityRows.TryValue(seen, value, out var candidate))
        {
            return new SteamUiCommandResult(false, "The device setting value is invalid.");
        }

        var result = await coordinator.Commands.ExecuteAsync(seen.CapabilityId, seen.InstanceId, candidate,
            NativeQamUi.CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return NativeQamUi.CommandResult(result, "The device refused the setting.");
    }

    /// <summary>Validates a JSON value against one currently offered, enabled row.</summary>
    /// <param name="row">The exact descriptor shown to the user.</param>
    /// <param name="value">Proposed JSON value; choices, range steps, text length and opaque RGB colors are checked by kind.</param>
    /// <returns>True only for an enabled writable kind with a valid value; action rows require boolean true.</returns>
    internal static bool ValidValue(SteamSettingsRow row, JsonElement value)
    {
        if (row.Disabled)
        {
            return false;
        }

        return row.Kind switch
        {
            SteamSettingsRowKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            SteamSettingsRowKind.Choice => value.ValueKind is JsonValueKind.String
                                           && row.Choices?.Any(choice =>
                                               choice.Selectable && choice.Value == value.GetString()) == true,
            SteamSettingsRowKind.Range => value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var number)
                && double.IsFinite(number) && row.Minimum is { } minimum && row.Maximum is { } maximum
                && number >= minimum && number <= maximum && row.Step is > 0
                && Math.Abs((number - minimum) / row.Step.Value - Math.Round((number - minimum) / row.Step.Value)) <
                0.000001,
            SteamSettingsRowKind.Text => value.ValueKind is JsonValueKind.String
                                         && (row.MaximumLength is null ||
                                             value.GetString()!.Length <= row.MaximumLength),
            SteamSettingsRowKind.ResolutionList => ValidResolutionEdit(row, value),
            SteamSettingsRowKind.Color => SteamCapabilityRows.TryColor(value, out _),
            SteamSettingsRowKind.Curve => value.ValueKind is JsonValueKind.Array,
            SteamSettingsRowKind.Action => value.ValueKind is JsonValueKind.True,
            _ => false
        };
    }

    /// <summary>Keeps commands from a former editing scope out of the current offered set.</summary>
    /// <param name="key">Base semantic command key.</param>
    /// <param name="active">Application and game-profile identity the row describes.</param>
    /// <param name="editsGame">Whether that row edits the game override rather than global defaults.</param>
    /// <returns>
    ///     The base key with a hash of the complete editing-scope identity, so stale-scope commands cannot match the new
    ///     offered set.
    /// </returns>
    internal static string ProfileKey(string key, ActiveProfile active, bool editsGame)
    {
        var identity = string.Join('\0', active.ApplicationId ?? string.Empty, active.GameProfileId ?? string.Empty,
            editsGame ? "game" : "global");
        return key + "/profile/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static int Integer(JsonElement value)
    {
        return (int)Math.Round(value.GetDouble());
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool Busy(string progress)
    {
        return progress is "queued" or "applying" or "replacing";
    }

    /// <summary>One feature's choice as a Steam dropdown, or null when it has nothing to offer.</summary>
    /// <param name="key">The row key.</param>
    /// <param name="label">The row's label.</param>
    /// <param name="icon">The toolkit glyph beside it in Quick Access.</param>
    /// <param name="state">The feature's choice; no options omits the row.</param>
    /// <returns>
    ///     The row: an unselectable option listed only while it is current, disabled while the choice is unavailable or
    ///     offers fewer than two entries, its description marked when the running game supplies the value, and the
    ///     selected label as its summary.
    /// </returns>
    internal static SteamSettingsRow? ChoiceRow(string key, string label, string icon, ChoiceRowState? state)
    {
        if (state is not { Options.Count: > 0 })
        {
            return null;
        }

        SteamSettingsChoice[] choices =
        [
            .. state.Options.Where(option => option.Selectable || option.Id == state.Current)
                .Select(option => new SteamSettingsChoice(option.Id, option.Label, option.Selectable))
        ];
        var selected = choices.FirstOrDefault(choice => choice.Value == state.Current);
        return new SteamSettingsRow(key, SteamSettingsRowKind.Choice, label,
            Marked(state.StatusText, state.Accent), Text: selected?.Value ?? string.Empty, Choices: choices,
            Disabled: !state.Available || choices.Length < 2, Accent: state.Accent, Icon: icon,
            Summary: selected?.Label);
    }

    /// <summary>A row description, led by the game-override label when the running game supplies the value.</summary>
    /// <param name="description">The row's own description, or empty.</param>
    /// <param name="accent">Whether the value is a game override.</param>
    /// <returns>The description to publish, or null for none.</returns>
    internal static string? Marked(string? description, bool accent)
    {
        return accent
            ? NativeQamLayout.AccentDescription(description)
            : string.IsNullOrEmpty(description)
                ? null
                : description;
    }

    private static void Section(List<SteamSettingsSection> target, string title, string id, List<SteamSettingsRow> rows)
    {
        if (rows.Count > 0)
        {
            target.Add(new SteamSettingsSection(title, rows, id));
        }
    }

    /// <summary>The command set one read offers, and the editing scope its profile-aware rows describe.</summary>
    /// <param name="profile">The profile snapshot the read started from.</param>
    private sealed class OfferedRows(ProfileSnapshot profile)
    {
        /// <summary>The profile snapshot every row of this read describes.</summary>
        internal ProfileSnapshot Profile { get; } = profile;

        /// <summary>The offered commands by row key.</summary>
        internal Dictionary<string, Offered> Commands { get; } = new(StringComparer.Ordinal);

        /// <summary>The profile the page's profile-aware controls edit: the running game's, or Global.</summary>
        internal string ScopeName { get; } = profile.EditsGame
            ? $"{profile.Game?.Name ?? profile.Active.ApplicationId}'s profile"
            : "Global defaults";

        /// <summary>What editing that profile means.</summary>
        internal string ScopeDetail { get; } = profile.EditsGame
            ? "Changes apply while this game runs. Settings it does not change follow Global."
            : "Live values are shared with Quick Access and the overlay.";

        /// <summary>The Quick Access sections this read built, in the order they were first given a row.</summary>
        internal List<SteamQuickAccessRowSection> QuickAccess { get; } = [];

        /// <summary>Adds a row and offers its command, keyed by the editing scope when it is profile-aware.</summary>
        /// <param name="rows">The Settings section rows the row joins, or null for a Quick Access-only row.</param>
        /// <param name="row">The row.</param>
        /// <param name="write">The command a matching write runs.</param>
        /// <param name="profileAware">Whether the row edits the current profile scope.</param>
        /// <returns>The row as offered, with its final key, for placing it on the other surface too.</returns>
        /// <remarks>A key offered twice in one read, by a row both surfaces draw, keeps its first command.</remarks>
        internal SteamSettingsRow Add(List<SteamSettingsRow>? rows, SteamSettingsRow row,
            Func<JsonElement, CancellationToken, Task<SteamUiCommandResult>> write, bool profileAware = false)
        {
            if (profileAware)
            {
                row = row with { Key = ProfileKey(row.Key, Profile.Active, Profile.EditsGame) };
            }

            rows?.Add(row);
            Commands.TryAdd(row.Key, new Offered(row, write, profileAware ? Profile.Active : null, Profile.EditsGame));
            return row;
        }

        /// <summary>Places a row in the Quick Access section the layout names by <paramref name="section" />.</summary>
        /// <param name="section">The section id, which is the layout kind it is drawn under.</param>
        /// <param name="row">The row, already offered when it takes a command.</param>
        internal void Quick(string section, SteamSettingsRow row)
        {
            var index = QuickAccess.FindIndex(candidate => candidate.Id == section);
            if (index < 0)
            {
                QuickAccess.Add(new SteamQuickAccessRowSection(section, [row]));
                return;
            }

            QuickAccess[index] = QuickAccess[index] with { Rows = [.. QuickAccess[index].Rows, row] };
        }

        /// <summary>Adds a whole Quick Access section, such as a titled one no layout section names.</summary>
        /// <param name="section">The section; its rows are already offered.</param>
        internal void QuickSection(SteamQuickAccessRowSection section)
        {
            QuickAccess.Add(section);
        }
    }

    private sealed record Offered(
        SteamSettingsRow Row,
        Func<JsonElement, CancellationToken, Task<SteamUiCommandResult>> Write,
        ActiveProfile? Active,
        bool EditsGame = false);
}
