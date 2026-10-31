using System;
using System.Collections.Generic;
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
/// <param name="scaledResolutions">Borrowed GPU-scaled resolution owner, or null to omit the source-mode editor.</param>
internal sealed partial class SteamNativeSettingsService(
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
    DisplayTimeouts? displayTimeouts = null,
    ScaledResolutionService? scaledResolutions = null) : ISteamNativeSettingsBackend
{
    private readonly SemaphoreSlim _reads = new(1, 1);
    private IReadOnlyDictionary<string, Offered> _offered = new Dictionary<string, Offered>();
    private long _revision;
    private string? _publishedPages;

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
    internal static long NextRevision(IReadOnlyList<SteamNativeSettingsPage> pages, ref string? published,
        long revision)
    {
        var content = JsonSerializer.Serialize(pages);
        if (string.Equals(content, published, StringComparison.Ordinal))
        {
            return revision;
        }

        published = content;
        return revision + 1;
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
            var offer = new OfferedRows(profiles.Current);
            // The backends are independent, so their reads run together and each page awaits its own.
            // A default read completes with nothing, which omits the rows of an owner that does not exist.
            var lightRead = brightness.ReadAsync();
            var presetsRead = powerPresets.ReadAsync();
            var schemesRead = powerProfiles.ReadAsync();
            var boostRead = cpuBoost is null ? default : cpuBoost.ReadAsync();
            var coresRead = hybridCores.ReadAsync();
            var endpointRead = audioFormat is null ? default : audioFormat.ReadEndpointAsync();
            List<SteamSettingsSection> display = [];
            List<SteamSettingsSection> power = [];
            List<SteamSettingsSection> audio = [];
            List<SteamSettingsSection> controller = [];
            await AddDisplayPageAsync(display, offer, lightRead).ConfigureAwait(false);
            await AddPowerPageAsync(power, offer, presetsRead, schemesRead, boostRead, coresRead)
                .ConfigureAwait(false);
            DeviceSections(power, controller, offer.Commands, offer.Profile);
            await AddAudioPageAsync(audio, offer, endpointRead).ConfigureAwait(false);
            AddRumbleSection(controller, offer);

            AddProfileSection(display, "display", offer);
            AddProfileSection(power, "power", offer);
            AddProfileSection(controller, "controller", offer);
            List<SteamNativeSettingsPage> pages = [];
            Page("display", display);
            Page("power", power);
            Page("audio", audio);
            Page("controller", controller);
            Volatile.Write(ref _offered, offer.Commands);
            _revision = NextRevision(pages, ref _publishedPages, _revision);
            return new SteamNativeSettingsState(pages, _revision);

            void Page(string id, List<SteamSettingsSection> sections)
            {
                if (sections.Count > 0)
                {
                    pages.Add(new SteamNativeSettingsPage(id, sections));
                }
            }
        }
        finally
        {
            _reads.Release();
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
            [new(page + ".scope", SteamSettingsRowKind.Note, "Editing scope", Text: offer.Scope)];
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

        sections.Insert(0, new SteamSettingsSection("Profile", rows, page + ".profile"));
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
        Dictionary<string, Offered> offered, ProfileSnapshot profile)
    {
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
            var key = ProfileKey(DeviceHostRowIds.LightingProfile, profile.Active, profile.EditsGame);
            var row = new SteamSettingsRow(key, SteamSettingsRowKind.Choice, descriptor.Title, descriptor.Description,
                Text: lightingSelection.Selected.Value ?? string.Empty, Choices: choices,
                Disabled: !descriptor.CanInvoke,
                Accent: lightingSelection.Selected.IsGameOverride);
            Section(controller, "Lighting", "device/lighting-profile", [row]);
            offered.Add(key, new Offered(row, async (value, token) =>
            {
                await coordinator.Lighting.SelectAsync(EmptyToNull(value.GetString()), token)
                    .ConfigureAwait(false);
                return coordinator.Lighting.Status == DescriptorStatus.Warning
                    ? new SteamUiCommandResult(false, coordinator.Lighting.Detail)
                    : SteamUiCommandResult.Applied;
            }, profile.Active, profile.EditsGame));
        }

        var declarations = CapabilityRowProjection.ProjectSections(coordinator.Capabilities.Sections);
        HashSet<string> declared = new(declarations.Select(section => section.SectionId), StringComparer.Ordinal);
        var capabilities = coordinator.PresentedCapabilities()
            .Select(view => CapabilityRowProjection.ToOverlayCapability(view, declared, coordinator.FanPresets,
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
            || coordinator.Capabilities.Snapshot().FirstOrDefault(view =>
                view.Descriptor.CapabilityId == seen.CapabilityId
                && view.Descriptor.InstanceId == seen.InstanceId) is not { } current)
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
                                           && row.Choices?.Any(choice => choice.Value == value.GetString()) == true,
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

    /// <summary>The command set one read offers, and the editing scope its profile-aware rows describe.</summary>
    /// <param name="profile">The profile snapshot the read started from.</param>
    private sealed class OfferedRows(ProfileSnapshot profile)
    {
        /// <summary>The profile snapshot every row of this read describes.</summary>
        internal ProfileSnapshot Profile { get; } = profile;

        /// <summary>The offered commands by row key.</summary>
        internal Dictionary<string, Offered> Commands { get; } = new(StringComparer.Ordinal);

        /// <summary>What profile-aware controls currently edit.</summary>
        internal string Scope { get; } = profile.EditsGame
            ? $"Profile-aware controls edit {profile.Game?.Name ?? profile.Active.ApplicationId}'s current game profile."
            : "Profile-aware controls edit Global defaults. Live values are shared with Quick Access and the overlay.";

        /// <summary>Adds a row and offers its command, keyed by the editing scope when it is profile-aware.</summary>
        /// <param name="rows">The section rows the row joins.</param>
        /// <param name="row">The row.</param>
        /// <param name="write">The command a matching write runs.</param>
        /// <param name="profileAware">Whether the row edits the current profile scope.</param>
        internal void Add(List<SteamSettingsRow> rows, SteamSettingsRow row,
            Func<JsonElement, CancellationToken, Task<SteamUiCommandResult>> write, bool profileAware = false)
        {
            if (profileAware)
            {
                row = row with { Key = ProfileKey(row.Key, Profile.Active, Profile.EditsGame) };
            }

            rows.Add(row);
            Commands.Add(row.Key, new Offered(row, write, profileAware ? Profile.Active : null, Profile.EditsGame));
        }
    }

    private sealed record Offered(
        SteamSettingsRow Row,
        Func<JsonElement, CancellationToken, Task<SteamUiCommandResult>> Write,
        ActiveProfile? Active,
        bool EditsGame = false);
}
