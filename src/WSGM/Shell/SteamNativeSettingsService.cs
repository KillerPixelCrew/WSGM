using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>Projects the existing Quick Access owners into Steam's native settings pages.</summary>
/// <remarks>No polling, hardware lifetime or configuration belongs to this projection.</remarks>
/// <param name="brightness">Borrowed live panel-brightness adapter.</param>
/// <param name="resolution">Borrowed display-mode adapter, or null to omit resolution.</param>
/// <param name="performance">Borrowed RTSS, frame-cap and VRR adapter.</param>
/// <param name="tdp">Borrowed sustained/boost power-limit adapter.</param>
/// <param name="autoTdp">Borrowed AutoTDP setting and status adapter.</param>
/// <param name="powerProfiles">Borrowed Windows power-profile adapter.</param>
/// <param name="powerPresets">Borrowed device preset-assignment adapter.</param>
/// <param name="cpuBoost">Borrowed CPU boost adapter, or null to omit the row.</param>
/// <param name="hybridCores">Borrowed processor core-placement adapter.</param>
/// <param name="audioFormat">Borrowed endpoint-format adapter, or null to omit advanced audio controls.</param>
/// <param name="graphics">Borrowed graphics settings projection, or null when no graphics owner is available.</param>
/// <param name="coordinator">Borrowed device capability and rumble owner, or null for an inactive device integration.</param>
/// <param name="profiles">Borrowed owner of the active application, editing scope and profile operations.</param>
/// <param name="displayTimeouts">Borrowed display-timeout owner, or null to omit timeout controls.</param>
internal sealed class SteamNativeSettingsService(
    NativeQamBrightnessService brightness,
    NativeQamResolutionService? resolution,
    PerformanceServiceNativeQamAdapter performance,
    DeviceCoordinatorNativeQamTdpService tdp,
    DeviceCoordinatorNativeQamAutoTdpService autoTdp,
    NativeQamPowerProfileService powerProfiles,
    NativeQamPowerPresetService powerPresets,
    NativeQamCpuBoostService? cpuBoost,
    NativeQamHybridCoreService hybridCores,
    NativeQamAudioFormatService? audioFormat,
    SteamGraphicsService? graphics,
    DeviceCoordinator? coordinator,
    ProfileService profiles,
    DisplayTimeouts? displayTimeouts = null) : ISteamNativeSettingsBackend
{
    private readonly SemaphoreSlim _reads = new(1, 1);
    private IReadOnlyDictionary<string, Offered> _offered = new Dictionary<string, Offered>();
    private long _revision;

    /// <inheritdoc />
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

    /// <summary>Reads fresh state from the same adapters that publish Quick Access.</summary>
    /// <returns>
    ///     Fresh native settings pages and revision; the completed read atomically replaces the exact offered command
    ///     set.
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
            var profile = profiles.Current;
            Dictionary<string, Offered> offered = new(StringComparer.Ordinal);
            List<SteamSettingsSection> display = [];
            List<SteamSettingsSection> power = [];
            List<SteamSettingsSection> audio = [];
            List<SteamSettingsSection> controller = [];
            var scope = profile.EditsGame
                ? $"Profile-aware controls edit {profile.Game?.Name ?? profile.Active.ApplicationId}'s current game profile."
                : "Profile-aware controls edit Global defaults. Live values are shared with Quick Access and the overlay.";

            List<SteamSettingsRow> displayRows = [];
            if (await brightness.ReadAsync().ConfigureAwait(false) is { } light)
            {
                Add(displayRows, new SteamSettingsRow("display.brightness", SteamSettingsRowKind.Range, "Brightness",
                        "Current panel backlight.", Number: light.Percent, Minimum: 0, Maximum: 100, Step: 1,
                        Suffix: "%"),
                    (value, token) => brightness.SetBrightnessAsync(Integer(value), token));
            }

            if (resolution?.Current is { Available: true } modes)
            {
                Add(displayRows, new SteamSettingsRow("display.resolution", SteamSettingsRowKind.Choice, "Resolution",
                        modes.StatusText, Text: modes.Current,
                        Choices: [.. modes.Options.Select(mode => new SteamSettingsChoice(mode, mode))]),
                    (value, token) => resolution.SetResolutionAsync(value.GetString()!, token));
            }

            var frame = performance.FrameLimit;
            var support = performance.PerfSupport?.Invoke();
            if (frame.RefreshRates is { Count: > 1 } rates && performance.ApplyRefreshRate is not null)
            {
                var selectable = !frame.LimitEnabled || support?.RefreshRatesSelectable == true;
                Add(displayRows, new SteamSettingsRow("display.refresh", SteamSettingsRowKind.Choice, "Refresh rate",
                        selectable
                            ? "Current display mode."
                            : "The frame limit currently owns the paired refresh rate.",
                        Text: frame.CurrentRefreshHz?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        Choices:
                        [
                            .. rates.Select(rate => new SteamSettingsChoice(rate.ToString(CultureInfo.InvariantCulture),
                                $"{rate} Hz"))
                        ], Disabled: !selectable),
                    (value, token) => performance.SetRefreshRateAsync(
                        int.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                        token), true);
            }

            if (frame.Available && frame.MinimumFps is { } minFps && frame.MaximumFps is { } maxFps)
            {
                Add(displayRows, new SteamSettingsRow("display.limit-enabled", SteamSettingsRowKind.Boolean,
                        "Frame limit", scope, frame.LimitEnabled, Accent: frame.Accent),
                    (value, token) => performance.ApplyPerfChangeAsync(new SteamPerformanceChange(
                            SteamPerformanceSetting.FrameLimitEnabled, value.GetBoolean() ? 1 : 0), "native-settings",
                        token),
                    true);
                Add(displayRows, new SteamSettingsRow("display.frame-limit", SteamSettingsRowKind.Range,
                        "Maximum frame rate", frame.StatusText,
                        Number: frame.DesiredFps is > 0 ? frame.DesiredFps : maxFps,
                        Minimum: minFps, Maximum: maxFps, Step: 1, Suffix: " FPS", Disabled: !frame.LimitEnabled,
                        Accent: frame.Accent),
                    (value, token) => performance.SetFrameLimitAsync(Integer(value), "native-settings", token),
                    true);
            }

            var vrr = performance.Vrr;
            if (vrr.Available)
            {
                Add(displayRows, new SteamSettingsRow("display.vrr", SteamSettingsRowKind.Boolean,
                        "Variable refresh rate", vrr.StatusText, vrr.Enabled, Accent: vrr.Accent),
                    (value, token) => performance.SetVariableRefreshRateAsync(value.GetBoolean(), token),
                    true);
            }

            Section(display, "Display and frame pacing", "display", displayRows);
            if (displayTimeouts is not null)
            {
                List<SteamSettingsRow> timeouts = [];
                foreach (var timeout in displayTimeouts.ReadState().Rows)
                {
                    var id = timeout.Id;
                    Add(timeouts, new SteamSettingsRow("display.timeout-" + id, SteamSettingsRowKind.Choice,
                            timeout.Label, timeout.Description,
                            Text: timeout.Seconds.ToString(CultureInfo.InvariantCulture),
                            Choices:
                            [
                                .. timeout.Options.Select(option => new SteamSettingsChoice(
                                    option.Seconds.ToString(CultureInfo.InvariantCulture), option.Label))
                            ],
                            Disabled: !timeout.Available),
                        (value, token) => displayTimeouts.SetTimeoutAsync(id,
                            int.Parse(value.GetString()!, CultureInfo.InvariantCulture), token));
                }

                Section(display, "Display idle behavior", "display-timeouts", timeouts);
            }

            foreach (var page in graphics?.ReadState().Pages ?? [])
            {
                foreach (var section in page.Sections)
                {
                    List<SteamSettingsRow> rows = [];
                    foreach (var row in section.Rows)
                    {
                        var key = row.Key;
                        Add(rows, row with { Key = "gpu/" + key },
                            (value, token) => graphics!.SetAsync(key, value, token), true);
                    }

                    Section(display, section.Title is null ? page.Title : page.Title + ": " + section.Title,
                        "gpu/" + page.Id + "/" + section.Id, rows);
                }
            }

            var presets = await powerPresets.ReadAsync().ConfigureAwait(false);
            if (presets is { Available: true })
            {
                List<SteamSettingsRow> rows = [];
                var options = new[] { new SteamSettingsChoice(string.Empty, presets.UnsetLabel) }
                    .Concat(presets.Options.Where(option => option.Selectable)
                        .Select(option => new SteamSettingsChoice(option.Id, option.Label))).ToArray();
                Add(rows, new SteamSettingsRow("power.preset-ac", SteamSettingsRowKind.Choice,
                        "Performance mode (plugged in)", presets.Scope, Text: presets.Ac, Choices: options,
                        Accent: presets.AcAccent),
                    (value, token) => powerPresets.SetAssignmentAsync(true, EmptyToNull(value.GetString()), token),
                    true);
                Add(rows, new SteamSettingsRow("power.preset-battery", SteamSettingsRowKind.Choice,
                        "Performance mode (on battery)", presets.Scope, Text: presets.Battery, Choices: options,
                        Accent: presets.BatteryAccent),
                    (value, token) => powerPresets.SetAssignmentAsync(false, EmptyToNull(value.GetString()), token),
                    true);
                rows.Add(new SteamSettingsRow("power.preset-current", SteamSettingsRowKind.Note,
                    "Current performance mode", presets.StatusText, Text: presets.Current));
                Section(power, "Device performance", "presets", rows);
            }

            List<SteamSettingsRow> limits = [];
            var powerLimit = tdp.PowerLimit;
            if (powerLimit.CanSelectMode)
            {
                Add(limits, new SteamSettingsRow("power.unified", SteamSettingsRowKind.Boolean,
                        "Unified TDP", "One target moves sustained and boost limits together.", powerLimit.Unified,
                        Accent: powerLimit.ModeAccent),
                    (value, token) => tdp.SetUnifiedModeAsync(value.GetBoolean(), token), true);
            }

            PowerRange("power.sustained", powerLimit.Unified ? "TDP" : "Sustained power", powerLimit.Sustained,
                tdp.SetPrimaryLimitAsync);
            if (!powerLimit.Unified)
            {
                PowerRange("power.boost", "Boost power", powerLimit.Boost, tdp.SetBoostLimitAsync);
            }

            var automatic = autoTdp.Current;
            if (automatic.Available)
            {
                Add(limits, new SteamSettingsRow("power.auto-tdp", SteamSettingsRowKind.Boolean, "AutoTDP",
                        automatic.StatusText, automatic.Enabled, Disabled: Busy(automatic.Progress)),
                    (value, token) => autoTdp.SetAutoTdpAsync(value.GetBoolean(), token));
            }

            Section(power, "TDP and AutoTDP", "tdp", limits);
            if (await powerProfiles.ReadAsync().ConfigureAwait(false) is { Available: true } schemes)
            {
                List<SteamSettingsRow> rows = [];
                Add(rows, Choice("power.scheme", "Windows power profile", schemes.Options,
                        schemes.Current, schemes.StatusText),
                    (value, token) => powerProfiles.SetPowerProfileAsync(value.GetString()!, token));
                Section(power, "Windows power policy", "windows-power", rows);
            }

            List<SteamSettingsRow> cpu = [];
            if (cpuBoost is not null && await cpuBoost.ReadAsync().ConfigureAwait(false) is { Available: true } boost)
            {
                Add(cpu, Choice("power.cpu-boost", "Processor boost", boost.Options, boost.Current, boost.StatusText)
                        with
                        {
                            Accent = boost.Accent
                        },
                    (value, token) => cpuBoost.SetCpuBoostAsync(value.GetString()!, token), true);
            }

            if (await hybridCores.ReadAsync().ConfigureAwait(false) is { Available: true } cores)
            {
                Add(cpu, Choice("power.hybrid", "Processor core preference", cores.Options, cores.Current,
                        cores.StatusText),
                    (value, token) => hybridCores.SetHybridCoresAsync(value.GetString()!, token));
            }

            Section(power, "CPU behavior", "cpu", cpu);
            DeviceSections(power, controller, offered, profile);

            if (audioFormat is not null)
            {
                var endpoint = await audioFormat.ReadEndpointAsync().ConfigureAwait(false);
                if (endpoint.EndpointId is { } endpointId && endpoint.State is { Available: true } playback)
                {
                    var endpointKey =
                        "audio/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpointId)));
                    List<SteamSettingsRow> rows = [];
                    AudioChoice(endpointKey + "/channels", "Playback channel layout", playback.ChannelOptions,
                        playback.CurrentChannels,
                        (value, token) => audioFormat.SetFormatForEndpointAsync(endpointId, value, token));
                    AudioChoice(endpointKey + "/format", "Default audio format", playback.FormatOptions,
                        playback.CurrentFormat,
                        (value, token) => audioFormat.SetFormatForEndpointAsync(endpointId, value, token));
                    AudioChoice(endpointKey + "/spatial", "Spatial sound", playback.SpatialOptions,
                        playback.CurrentSpatial,
                        (value, token) => audioFormat.SetSpatialForEndpointAsync(endpointId, value, token));
                    Section(audio, "Advanced playback", "advanced-playback", rows);

                    void AudioChoice(string key, string label, IReadOnlyList<SteamAudioFormatOption> options,
                        string current,
                        Func<string, CancellationToken, Task<SteamUiCommandResult>> write)
                    {
                        if (options.Count == 0)
                        {
                            return;
                        }

                        Add(rows, new SteamSettingsRow(key, SteamSettingsRowKind.Choice, label, playback.StatusText,
                                Text: current,
                                Choices:
                                [
                                    .. options.Select(option => new SteamSettingsChoice(option.Id, option.Label))
                                ]),
                            (value, token) => write(value.GetString()!, token));
                    }
                }
            }

            if (coordinator is not null)
            {
                var calibration = coordinator.RumbleCalibration;
                var available = coordinator.CanPreviewRumble;
                List<SteamSettingsRow> rumble = [];
                CalibrationRange("strength", "Overall rumble strength", calibration.StrengthPercent, 100, "%",
                    "Global user preference. Zero silences output; 100% preserves the original requested strength.");
                CalibrationRange("floor", "Minimum feelable pulse strength", calibration.MinimumStrengthPercent, 100,
                    "%",
                    "The minimum nonzero bounded-pulse drive. Continuous rumble is never raised to this floor. Device motor limits remain authoritative.");
                CalibrationRange("duration", "Minimum feelable pulse duration", calibration.MinimumPulseMilliseconds,
                    500, " ms",
                    "Bounded pulses are stretched to this duration or the device minimum, whichever is longer.");
                Add(rumble, new SteamSettingsRow("rumble.test-floor", SteamSettingsRowKind.Action,
                    "Test minimum strength and duration",
                    "One bounded pulse at the selected minimum motor drive, without overall gain. Adjust until reliably feelable.",
                    Disabled: !available, ButtonLabel: "Test pulse"), (_, token) => Preview(true, token));
                Add(rumble, new SteamSettingsRow("rumble.preview", SteamSettingsRowKind.Action,
                    "Preview overall calibration",
                    "A 25% reference pulse shaped by overall strength, pulse floor and duration. Maximum preview: 500 ms.",
                    Disabled: !available, ButtonLabel: "Preview"), (_, token) => Preview(false, token));
                Add(rumble, new SteamSettingsRow("rumble.stop", SteamSettingsRowKind.Action, "Stop rumble preview",
                    ButtonLabel: "Stop"), (_, _) => StopRumblePreviewAsync());
                if (!available)
                {
                    rumble.Add(new SteamSettingsRow("rumble.availability", SteamSettingsRowKind.Note,
                        "Preview availability",
                        Text:
                        "Connect a WSGM-managed controller with published motor output. Saved calibration applies when that output becomes available."));
                }

                Section(controller, "Rumble calibration", "rumble", rumble);

                void CalibrationRange(string key, string label, int current, int maximum, string suffix,
                    string description)
                {
                    Add(rumble, new SteamSettingsRow("rumble." + key, SteamSettingsRowKind.Range, label,
                        description + " Saved on completion of the edit.", Number: current, Minimum: 0,
                        Maximum: maximum, Step: 1, Suffix: suffix), async (value, token) =>
                    {
                        await coordinator.SetRumbleCalibrationAsync(key, Integer(value), token).ConfigureAwait(false);
                        return SteamUiCommandResult.Applied;
                    });
                }

                async Task<SteamUiCommandResult> Preview(bool testFloor, CancellationToken token)
                {
                    return await coordinator.PreviewRumbleAsync(testFloor, token).ConfigureAwait(false)
                        ? SteamUiCommandResult.Applied
                        : new SteamUiCommandResult(false,
                            "A preview is already active or controller output is unavailable.");
                }
            }

            ProfileSection(display, "display");
            ProfileSection(power, "power");
            ProfileSection(controller, "controller");
            List<SteamNativeSettingsPage> pages = [];
            Page("display", display);
            Page("power", power);
            Page("audio", audio);
            Page("controller", controller);
            Volatile.Write(ref _offered, offered);
            return new SteamNativeSettingsState(pages, Interlocked.Increment(ref _revision));

            void Page(string id, List<SteamSettingsSection> sections)
            {
                if (sections.Count > 0)
                {
                    pages.Add(new SteamNativeSettingsPage(id, sections));
                }
            }

            void Add(List<SteamSettingsRow> rows, SteamSettingsRow row,
                Func<JsonElement, CancellationToken, Task<SteamUiCommandResult>> write, bool profileAware = false)
            {
                if (profileAware)
                {
                    row = row with { Key = ProfileKey(row.Key, profile.Active, profile.EditsGame) };
                }

                rows.Add(row);
                offered.Add(row.Key, new Offered(row, write, profileAware ? profile.Active : null, profile.EditsGame));
            }

            void PowerRange(string key, string label, SteamPowerLimitRangeState range,
                Func<int, CancellationToken, Task<SteamUiCommandResult>> write)
            {
                if (range is not
                    { Available: true, MinimumWatts: { } minimum, MaximumWatts: { } maximum, StepWatts: { } step })
                {
                    return;
                }

                Add(limits, new SteamSettingsRow(key, SteamSettingsRowKind.Range, label, range.StatusText,
                        Number: range.ObservedWatts ?? maximum, Minimum: minimum, Maximum: maximum, Step: step,
                        Suffix: " W", Disabled: Busy(range.Progress), Accent: range.Accent),
                    (value, token) => write(Integer(value), token), true);
            }

            void ProfileSection(List<SteamSettingsSection> sections, string page)
            {
                if (sections.Count == 0)
                {
                    return;
                }

                List<SteamSettingsRow> rows =
                    [new(page + ".scope", SteamSettingsRowKind.Note, "Editing scope", Text: scope)];
                if (profile.Active.HasApplication)
                {
                    Add(rows, new SteamSettingsRow(page + ".game-profile", SteamSettingsRowKind.Boolean,
                            "Use a profile for the running game",
                            "Shares Steam Quick Access's per-game profile switch.",
                            profile.EditsGame),
                        async (value, token) => new SteamUiCommandResult(
                            await profiles.SetGameEnabledAsync(value.GetBoolean(), profile.Active.ApplicationId, token)
                                .ConfigureAwait(false), null), true);
                }

                sections.Insert(0, new SteamSettingsSection("Profile", rows, page + ".profile"));
                if (page == "controller" && !profile.EditsGame)
                {
                    return;
                }

                List<SteamSettingsRow> reset = [];
                Add(reset, new SteamSettingsRow(page + ".reset", SteamSettingsRowKind.Action,
                        profile.EditsGame ? "Use global values" : "Reset performance defaults",
                        profile.EditsGame
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
        }
        finally
        {
            _reads.Release();
        }
    }

    private void DeviceSections(List<SteamSettingsSection> power, List<SteamSettingsSection> controller,
        Dictionary<string, Offered> offered, ProfileSnapshot profile)
    {
        if (coordinator is not { IntegrationEnabled: true })
        {
            return;
        }

        if (coordinator.LightingProfileSelection() is { } lightingSelection)
        {
            var descriptor = DeviceOverlayBridge.LightingProfileView(lightingSelection.Profiles,
                lightingSelection.Selected,
                coordinator.LightingProfileDetail, coordinator.LightingProfileStatus);
            var choices = new[] { new SteamSettingsChoice(string.Empty, profile.EditsGame ? "Use Global" : "None") }
                .Concat(lightingSelection.Profiles.Select(item => new SteamSettingsChoice(item.ProfileId, item.Name)))
                .ToArray();
            var key = ProfileKey(DeviceHostRowIds.LightingProfile, profile.Active, profile.EditsGame);
            var row = new SteamSettingsRow(key, SteamSettingsRowKind.Choice, descriptor.Title, descriptor.Description,
                Text: lightingSelection.Selected.Value ?? string.Empty, Choices: choices,
                Disabled: !descriptor.CanInvoke,
                Accent: lightingSelection.Selected.IsGameOverride);
            Section(controller, "Lighting", "device/lighting-profile", [row]);
            offered.Add(key, new Offered(row, async (value, token) =>
            {
                await coordinator.SelectLightingProfileAsync(EmptyToNull(value.GetString()), token)
                    .ConfigureAwait(false);
                return coordinator.LightingProfileStatus == DescriptorStatus.Warning
                    ? new SteamUiCommandResult(false, coordinator.LightingProfileDetail)
                    : SteamUiCommandResult.Applied;
            }, profile.Active, profile.EditsGame));
        }

        var declarations = DeviceOverlayBridge.ProjectSections(coordinator.Capabilities.Sections);
        HashSet<string> declared = new(declarations.Select(section => section.SectionId), StringComparer.Ordinal);
        var capabilities = coordinator.Capabilities.Snapshot()
            .Select(view => DeviceOverlayBridge.ToOverlayCapability(view, declared, profile.Layers))
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
                    var devicePower = capability.Role is CapabilityRole.ScenarioMode
                                      || capability.Section is DeviceOverlaySection.PowerAndThermals
                                      || declaration?.Key is SettingSectionKey.Power;
                    if (!lighting && !devicePower)
                    {
                        continue;
                    }

                    var row = DeviceRow(capability);
                    row = row with { Key = ProfileKey(row.Key, profile.Active, profile.EditsGame) };
                    (lighting ? lightingRows : powerRows).Add(row);
                    var captured = capability;
                    offered.Add(row.Key, new Offered(row, (value, token) => WriteDeviceAsync(captured, value, token),
                        profile.Active, profile.EditsGame));
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
    /// <returns>A color/text-specific row or the shared graphics row, with a generation-qualified key and override accent.</returns>
    internal static SteamSettingsRow DeviceRow(DeviceOverlayCapability capability)
    {
        var key = "device/" + capability.CapabilityId +
                  (capability.InstanceId is { } instance ? "#" + instance : string.Empty);
        var row = capability switch
        {
            { Writable: true, ValueKind: CapabilityValueKind.Color } => new SteamSettingsRow(key,
                SteamSettingsRowKind.Color,
                capability.Title, "Choose a color, then Save to write the device. " + capability.Description,
                Text: $"#{capability.CurrentValue?.ColorValue ?? 0xFFFFFF:X6}", Disabled: !capability.CanInvoke,
                ColorAlpha: false),
            { Writable: true, ValueKind: CapabilityValueKind.Text } => new SteamSettingsRow(key,
                SteamSettingsRowKind.Text,
                capability.Title, capability.Description, Text: capability.CurrentValue?.TextValue ?? string.Empty,
                MaximumLength: capability.MaximumLength, Disabled: !capability.CanInvoke),
            _ => SteamGraphicsService.Row(capability) with { Key = key }
        };
        return capability.OverrideId is null || row.Accent
            ? row
            : row with { Accent = true, Description = NativeQamLayout.AccentDescription(row.Description) };
    }

    private async Task<SteamUiCommandResult> WriteDeviceAsync(DeviceOverlayCapability seen, JsonElement value,
        CancellationToken cancellationToken)
    {
        if (coordinator is not { IntegrationEnabled: true }
            || coordinator.Capabilities.Snapshot().FirstOrDefault(view =>
                view.Descriptor.CapabilityId == seen.CapabilityId
                && view.Descriptor.InstanceId == seen.InstanceId) is not { } current)
        {
            return new SteamUiCommandResult(false, "The device capability changed while you were choosing.");
        }

        CapabilityValue? candidate;
        if (seen.ValueKind is CapabilityValueKind.Color && TryColor(value, out var color))
        {
            candidate = new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = color };
        }
        else if (seen.ValueKind is CapabilityValueKind.Text && value.ValueKind is JsonValueKind.String)
        {
            candidate = new CapabilityValue { Kind = CapabilityValueKind.Text, TextValue = value.GetString() };
        }
        else if (!SteamGraphicsService.TryValue(seen, value, out candidate))
        {
            return new SteamUiCommandResult(false, "The device setting value is invalid.");
        }

        var result = await coordinator.ExecuteCapabilityAsync(seen.CapabilityId, seen.InstanceId, candidate,
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
                                           && row.Choices?.Any(choice => choice.Value == value.GetString()) == true,
            SteamSettingsRowKind.Range => value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var number)
                && double.IsFinite(number) && row.Minimum is { } minimum && row.Maximum is { } maximum
                && number >= minimum && number <= maximum && row.Step is > 0
                && Math.Abs((number - minimum) / row.Step.Value - Math.Round((number - minimum) / row.Step.Value)) <
                0.000001,
            SteamSettingsRowKind.Text => value.ValueKind is JsonValueKind.String
                                         && (row.MaximumLength is null ||
                                             value.GetString()!.Length <= row.MaximumLength),
            SteamSettingsRowKind.Color => TryColor(value, out _),
            SteamSettingsRowKind.Action => value.ValueKind is JsonValueKind.True,
            _ => false
        };
    }

    /// <summary>Parses the native editor's opaque RGB color vocabulary.</summary>
    /// <param name="value">A #RRGGBB string or hsla(h, s%, l%, 1); finite hue is wrapped into one turn.</param>
    /// <param name="color">Packed 24-bit RGB on success; zero on failure.</param>
    /// <returns>True for a complete supported color with valid saturation/lightness and full opacity.</returns>
    internal static bool TryColor(JsonElement value, out int color)
    {
        color = 0;
        if (value.ValueKind is not JsonValueKind.String || value.GetString() is not { } text)
        {
            return false;
        }

        if (text.Length == 7 && text[0] == '#')
        {
            return int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out color);
        }

        if (!text.StartsWith("hsla(", StringComparison.Ordinal) || !text.EndsWith(')'))
        {
            return false;
        }

        var parts = text[5..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !parts[1].EndsWith('%') || !parts[2].EndsWith('%')
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hue)
            || !double.TryParse(parts[1][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var saturation)
            || !double.TryParse(parts[2][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lightness)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
            || !double.IsFinite(hue) || saturation is < 0 or > 100 || !double.IsFinite(saturation)
            || lightness is < 0 or > 100 || !double.IsFinite(lightness) || alpha != 1)
        {
            return false;
        }

        var normalizedHue = (hue % 360 + 360) % 360 / 60;
        var light = lightness / 100;
        var chroma = (1 - Math.Abs(2 * light - 1)) * saturation / 100;
        var secondary = chroma * (1 - Math.Abs(normalizedHue % 2 - 1));
        var offset = light - chroma / 2;
        var (red, green, blue) = normalizedHue switch
        {
            < 1 => (chroma, secondary, 0d),
            < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary),
            < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };
        color = (Channel(red) << 16) | (Channel(green) << 8) | Channel(blue);
        return true;

        int Channel(double channel)
        {
            return (int)Math.Round((channel + offset) * 255);
        }
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

    private static SteamSettingsRow Choice(string key, string label, IReadOnlyList<SteamPowerProfileOption> options,
        string current, string description)
    {
        return new SteamSettingsRow(key, SteamSettingsRowKind.Choice, label, description, Text: current,
            Choices:
            [
                .. options.Where(option => option.Selectable)
                    .Select(option => new SteamSettingsChoice(option.Id, option.Label))
            ]);
    }

    private static void Section(List<SteamSettingsSection> target, string title, string id, List<SteamSettingsRow> rows)
    {
        if (rows.Count > 0)
        {
            target.Add(new SteamSettingsSection(title, rows, id));
        }
    }

    private sealed record Offered(
        SteamSettingsRow Row,
        Func<JsonElement, CancellationToken, Task<SteamUiCommandResult>> Write,
        ActiveProfile? Active,
        bool EditsGame = false);
}
