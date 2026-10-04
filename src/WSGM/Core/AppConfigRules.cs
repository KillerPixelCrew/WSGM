using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

using static WSGM.Core.AppConfigDefaults;

namespace WSGM.Core;

internal static class AppConfigRules
{
    /// <summary>
    ///     An unknown enum NUMBER ("SpinnerStyle": 999 deserializes into the
    ///     enum unchecked) falls back to the field's default rather than to whatever
    ///     neighbouring member a clamp would land on.
    /// </summary>
    private static T Definite<T>(T value, T fallback) where T : struct, Enum
    {
        return Enum.IsDefined(value) ? value : fallback;
    }

    /// <summary>
    ///     An explicit JSON null ("StartupApps": null) deserializes over the
    ///     property initializer; replace nulls with fresh defaults so a hand-edited
    ///     config can never NRE the shell later (which would kill it before the panic
    ///     handler runs). New nested object/list members belong in this list too.
    /// </summary>
    internal static AppConfig Normalize(AppConfig config)
    {
        config.StartMode = Definite(config.StartMode, Defaults.StartMode);
        config.StartupApps ??= [];
        config.PluginInstances = [.. (config.PluginInstances ?? []).Where(static instance => instance is not null)];
        config.DeviceIntegration ??= new DeviceIntegrationConfig();
        NormalizeDeviceIntegration(config.DeviceIntegration);
        config.Performance ??= new PerformanceConfig();
        NormalizePerformance(config.Performance);
        config.Profiles ??= new ProfileConfig();
        NormalizeProfiles(config.Profiles, config.DeviceIntegration);
        config.Artwork ??= new ArtworkConfig();
        NormalizeArtwork(config.Artwork);
        config.GameLibrary ??= new GameLibraryConfig();
        NormalizeGameLibrary(config.GameLibrary);
        config.Themes ??= new ThemesConfig();
        NormalizeThemes(config.Themes);
        config.Animations ??= new AnimationsConfig();
        NormalizeAnimations(config.Animations);
        config.Sounds ??= new SoundsConfig();
        config.Sounds.Selected ??= string.Empty;
        config.Cef ??= new CefConfig();
        config.Hotkey ??= new HotkeyConfig();
        config.GamepadChord ??= new GamepadChordConfig();
        config.Gestures ??= new GestureConfig();
        config.QuickAccessPins ??= [];
        config.PluginWidgetPins = PluginWidgetPins.Normalize(config.PluginWidgetPins);
        config.QuickAccessPins =
        [
            .. config.QuickAccessPins
                .Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
        ];
        config.SavedDisplayScaleEntries ??= [];
        config.GameModeLaunch ??= new GameModeLaunchConfiguration();
        NormalizeGameModeLaunch(config.GameModeLaunch);
        config.GameModeLaunchRecovery ??= new GameModeLaunchRecovery();
        config.GameModeLaunchRecovery.PendingReturnAudio =
            NormalizeAudioProfile(config.GameModeLaunchRecovery.PendingReturnAudio);
        config.PreviousConsoleLockSchemeValues ??= [];
        config.CardLibraries ??= [];
        config.ForgottenInsertedCardIds ??= [];
        config.ForgottenInsertedCardIds =
        [
            .. config.ForgottenInsertedCardIds
                .Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
        ];
        config.CustomTabs ??= [];
        config.LibraryTabOrder ??= [];
        config.HiddenNativeTabs ??= [];
        config.KnownNativeTabs ??= [];
        config.PluginConfigurations ??= [];
        config.PluginConfigurations.RemoveAll(static entry => entry is null);
        config.LaunchWrappers ??= [];
        // A null ELEMENT ("StartupApps": [null]) survives the list-level ??= above and
        // would NRE in SelfElevation before the crash-loop breaker has recorded the
        // start — the shell would then die at every sign-in with nothing disarming it.
        // RemoveAll repairs in place: Normalize must hand back the caller's own list
        // instances (ConfigurationTests pins that), and rebuilding them would
        // allocate on every config load just to drop elements that are almost never there.
        config.StartupApps.RemoveAll(static app => app is null);
        foreach (var app in config.StartupApps)
        {
            app.Path ??= "";
            app.Args ??= "";
        }

        config.LaunchWrappers.RemoveAll(static w => w is null);
        foreach (var wrapper in config.LaunchWrappers)
        {
            wrapper.OriginalTarget ??= "";
            wrapper.OriginalLaunchOptions ??= "";
            wrapper.OriginalStartDir ??= "";
            wrapper.Name ??= "";
            wrapper.CustomActionPath ??= "";
            wrapper.CustomArguments ??= "";
        }

        config.CardLibraries = [.. config.CardLibraries.Where(static card => card is not null)];
        foreach (var card in config.CardLibraries)
        {
            card.ContentId ??= "";
            card.Name ??= "";
            card.AppIds ??= [];
        }

        config.CustomTabs = [.. config.CustomTabs.Where(static tab => tab is not null)];
        foreach (var tab in config.CustomTabs)
        {
            tab.Id = string.IsNullOrWhiteSpace(tab.Id) ? Guid.NewGuid().ToString("N") : tab.Id;
            tab.Name ??= "";
            tab.FilterTree ??= new FilterNode { Kind = FilterKind.Merge };
            NormalizeFilter(tab.FilterTree);
        }

        config.LibraryTabOrder = [.. config.LibraryTabOrder.Where(static key => key is not null)];
        config.HiddenNativeTabs = [.. config.HiddenNativeTabs.Where(static id => id is not null)];
        config.KnownNativeTabs = [.. config.KnownNativeTabs.Where(static tab => tab is not null)];
        foreach (var native in config.KnownNativeTabs)
        {
            native.Id ??= "";
            native.Title ??= "";
        }

        config.SavedDisplayScaleEntries.RemoveAll(static entry => entry is null);
        foreach (var entry in config.SavedDisplayScaleEntries)
        {
            entry.DeviceName ??= "";
        }

        config.PreviousConsoleLockSchemeValues.RemoveAll(static scheme => scheme is null);
        foreach (var scheme in config.PreviousConsoleLockSchemeValues)
        {
            scheme.SchemeGuid ??= "";
        }

        config.AccentColor ??= Defaults.AccentColor;
        config.OverlayBlurRadius = double.IsFinite(config.OverlayBlurRadius)
            ? Math.Clamp(config.OverlayBlurRadius, 0, 60)
            : Defaults.OverlayBlurRadius;
        config.Splash ??= new SplashConfig();
        NormalizeSplash(config.Splash);
        return config;
    }

    /// <summary>
    ///     Brings device-integration configuration into a shape the rest of WSGM can rely on.
    /// </summary>
    /// <param name="device">The section to normalize in place.</param>
    /// <remarks>
    ///     Internal rather than private so its rules can be tested directly. It touches only the object
    ///     handed to it and reads no file, which is what keeps a test off the developer's real
    ///     configuration.
    /// </remarks>
    internal static void NormalizeDeviceIntegration(DeviceIntegrationConfig device)
    {
        device.GlyphSelection = Definite(
            device.GlyphSelection, Defaults.DeviceIntegration.GlyphSelection);
        device.ManualGlyphProfileId = string.IsNullOrWhiteSpace(device.ManualGlyphProfileId)
            ? null
            : device.ManualGlyphProfileId.Trim();
        // A scope with no device, no plugin, or no values keys nothing and can never be matched, so
        // it would sit in the file forever growing it. Values are only shape-checked here; whether
        // one still satisfies its declared bounds is decided against the live manifest on load,
        // because a plugin update can narrow a range after the value was stored.
        device.PluginSettings ??= [];
        device.PluginSettings.RemoveAll(static scope => scope is null
                                                        || string.IsNullOrWhiteSpace(scope.DeviceDefinitionId)
                                                        || string.IsNullOrWhiteSpace(scope.PluginId));
        foreach (var scope in device.PluginSettings)
        {
            scope.DeviceDefinitionId = scope.DeviceDefinitionId.Trim();
            scope.PluginId = scope.PluginId.Trim();

            // Settings renders the cached declaration without activating plugin code. Drop malformed
            // declarations so every rendered control has the bounds required by the SDK contract.
            if (scope.Declaration is { } declaration && !declaration.TryValidate(out var reason))
            {
                Log.Warn(
                    $"Plugin settings: cached declaration for {scope.PluginId} on "
                    + $"{scope.DeviceDefinitionId} was dropped: {reason}");
                scope.Declaration = null;
            }

            // A profile with no id keys nothing and can never be selected, and one whose curve is
            // not strictly ascending is refused by the device router on apply — keeping either
            // would leave the user a profile that silently does nothing when chosen.
            scope.Profiles ??= [];
            scope.Profiles.RemoveAll(static profile => profile is null
                                                       || string.IsNullOrWhiteSpace(profile.ProfileId)
                                                       || string.IsNullOrWhiteSpace(profile.CapabilityId));
            HashSet<string> profileIds = new(StringComparer.Ordinal);
            scope.Profiles.RemoveAll(profile => !profileIds.Add(profile.ProfileId.Trim()));
            foreach (var profile in scope.Profiles)
            {
                profile.ProfileId = profile.ProfileId.Trim();
                profile.CapabilityId = profile.CapabilityId.Trim();
                profile.Name = string.IsNullOrWhiteSpace(profile.Name)
                    ? profile.ProfileId
                    : profile.Name.Trim();
                profile.Curve ??= [];
                profile.Curve.RemoveAll(static point => point is null);
            }

            scope.Profiles.RemoveAll(static profile =>
            {
                if (profile.Curve.Count == 0)
                {
                    return false;
                }

                for (var index = 1; index < profile.Curve.Count; index++)
                {
                    if (profile.Curve[index].Input > profile.Curve[index - 1].Input)
                    {
                        continue;
                    }

                    Log.Warn(
                        $"Device profile '{profile.ProfileId}' was dropped: its curve inputs "
                        + "are not strictly ascending.");
                    return true;
                }

                return false;
            });
            scope.Values ??= [];
            scope.Values.RemoveAll(static value => value is null
                                                   || string.IsNullOrWhiteSpace(value.SettingId));
            HashSet<string> settingIds = new(StringComparer.Ordinal);
            scope.Values.RemoveAll(value => !settingIds.Add(value.SettingId.Trim()));
            foreach (var value in scope.Values)
            {
                value.SettingId = value.SettingId.Trim();
            }
        }

        HashSet<(string DeviceDefinitionId, string PluginId)> scopeKeys = [];
        device.PluginSettings.RemoveAll(scope => !scopeKeys.Add((scope.DeviceDefinitionId, scope.PluginId)));

        device.OemAssignments ??= [];
        device.OemAssignments.RemoveAll(static assignment => assignment is null
                                                             || string.IsNullOrWhiteSpace(assignment.ControlId)
                                                             || !Enum.IsDefined(assignment.Action));
        HashSet<string> controls = new(StringComparer.Ordinal);
        device.OemAssignments.RemoveAll(assignment => !controls.Add(assignment.ControlId.Trim()));
        foreach (var assignment in device.OemAssignments)
        {
            assignment.ControlId = assignment.ControlId.Trim();
        }
    }

    private static void NormalizePerformance(PerformanceConfig performance)
    {
        performance.FrameLimitStrategy = Definite(
            performance.FrameLimitStrategy, Defaults.Performance.FrameLimitStrategy);
    }

    /// <summary>Brings the profile store into a shape the resolver can rely on.</summary>
    /// <param name="profiles">The store to normalize in place.</param>
    /// <param name="device">The device section, whose authored profiles a fan-curve reference must name.</param>
    /// <remarks>
    ///     Internal so its rules can be tested directly. A reference to an authored profile that no
    ///     longer exists is dropped, so that layer falls back to the one below it instead of naming
    ///     nothing.
    /// </remarks>
    internal static void NormalizeProfiles(ProfileConfig profiles, DeviceIntegrationConfig device)
    {
        HashSet<string> authored = new(device.PluginSettings.SelectMany(scope => scope.Profiles)
            .Select(profile => profile.ProfileId), StringComparer.Ordinal);
        profiles.Global ??= new ProfileValues();
        NormalizeProfileValues(profiles.Global, authored);
        profiles.Games ??= [];
        profiles.Games.RemoveAll(static game => game is null || string.IsNullOrWhiteSpace(game.Id));
        HashSet<string> ids = new(StringComparer.Ordinal);
        profiles.Games.RemoveAll(game => !ids.Add(game.Id.Trim()));
        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var game in profiles.Games)
        {
            game.Id = game.Id.Trim();
            game.Name = game.Name?.Trim() ?? string.Empty;
            // One executable activates one profile. A second claim would make the match ambiguous,
            // and an ambiguous match resolves to no profile at all.
            game.ProcessNames = (game.ProcessNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Where(name => string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                               && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(claimed.Add)
                .ToList();
            // Learned names match nothing, so two games may share one; only the shape is checked.
            game.Executables = (game.Executables ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Where(name => string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                               && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            game.Values ??= new ProfileValues();
            NormalizeProfileValues(game.Values, authored);
        }
    }

    /// <summary>Repairs a Game Library section a hand edit left naming no mode that exists.</summary>
    /// <param name="library">The section to repair in place.</param>
    internal static void NormalizeGameLibrary(GameLibraryConfig library)
    {
        if (!Enum.IsDefined(library.DefaultMode))
        {
            library.DefaultMode = ImportMode.SteamIntegration;
        }

        if (!Enum.IsDefined(library.ArtworkPreference))
        {
            library.ArtworkPreference = ArtworkPreference.Catalog;
        }

        library.DisabledSources =
        [
            .. (library.DisabledSources ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        // A folder needs an identity no other folder has and an absolute path; its file types are
        // the few a shortcut can be, so a hand edit cannot turn a folder of documents into titles.
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        library.ShortcutFolders =
        [
            .. (library.ShortcutFolders ?? [])
            .Where(folder => folder is { Id.Length: > 7, Path.Length: > 2 }
                             && folder.Id.StartsWith("folder:", StringComparison.Ordinal)
                             && Path.IsPathFullyQualified(folder.Path)
                             && ids.Add(folder.Id))
        ];
        foreach (var folder in library.ShortcutFolders)
        {
            folder.Extensions =
            [
                .. (folder.Extensions ?? [])
                .Where(extension => ShortcutFolderConfig.AllowedExtensions.Contains(extension, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
            ];
            if (folder.Extensions.Count == 0)
            {
                folder.Extensions = [.. ShortcutFolderConfig.AllowedExtensions];
            }
        }
    }

    /// <summary>Repairs the animations section: a trimmed boot id and whole set-aside strings.</summary>
    /// <param name="animations">The section.</param>
    internal static void NormalizeAnimations(AnimationsConfig animations)
    {
        animations.Boot = animations.Boot?.Trim() ?? string.Empty;
        if (animations.SteamSetAside is { } setAside)
        {
            setAside.MovieId ??= string.Empty;
            setAside.LocalPath ??= string.Empty;
        }
    }

    /// <summary>Repairs the themes section: a known translation branch and a clean hidden list.</summary>
    /// <param name="themes">The section.</param>
    internal static void NormalizeThemes(ThemesConfig themes)
    {
        themes.TranslationsBranch = themes.TranslationsBranch?.Trim().ToLowerInvariant() switch
        {
            ThemeTranslationBranch.Stable => ThemeTranslationBranch.Stable,
            ThemeTranslationBranch.Beta => ThemeTranslationBranch.Beta,
            _ => ThemeTranslationBranch.Auto
        };
        themes.HiddenThemes =
        [
            .. (themes.HiddenThemes ?? [])
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
        ];
    }

    /// <summary>Brings the artwork section into a shape the Steam browser can render.</summary>
    /// <param name="artwork">The section to normalize in place.</param>
    /// <remarks>
    ///     Internal so its rules can be tested directly. The tab order is a permutation of the known
    ///     tabs: unknown and duplicate ids are dropped and missing ones appended, so a hand-edited
    ///     value cannot hide a tab the show switches still say is visible, and the default tab always
    ///     names one that exists.
    /// </remarks>
    internal static void NormalizeArtwork(ArtworkConfig artwork)
    {
        artwork.SteamGridDbApiKey = artwork.SteamGridDbApiKey?.Trim() ?? "";
        artwork.ScreenscraperUser = artwork.ScreenscraperUser?.Trim() ?? "";
        artwork.ScreenscraperUserPassword = artwork.ScreenscraperUserPassword ?? "";

        var known = ArtworkConfig.DefaultTabOrder.Split(',');
        var ordered = (artwork.TabOrder ?? "").Split(',')
            .Select(static tab => tab.Trim())
            .Where(tab => known.Contains(tab, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        ordered.AddRange(known.Where(tab => !ordered.Contains(tab, StringComparer.Ordinal)));
        artwork.TabOrder = string.Join(',', ordered);

        var requested = artwork.DefaultTab?.Trim() ?? "";
        artwork.DefaultTab = known.Contains(requested, StringComparer.Ordinal) ? requested : ordered[0];
    }

    private static void NormalizeProfileValues(ProfileValues values, HashSet<string> authoredProfiles)
    {
        values.AcPowerPreset = NormalizePowerPreset(values.AcPowerPreset);
        values.BatteryPowerPreset = NormalizePowerPreset(values.BatteryPowerPreset);
        if (values.ControllerTarget is { } target && !Enum.IsDefined(target))
        {
            values.ControllerTarget = null;
        }

        if (values.FanCurveProfileId is { } fanCurve)
        {
            values.FanCurveProfileId = authoredProfiles.Contains(fanCurve.Trim()) ? fanCurve.Trim() : null;
        }

        values.Device ??= [];
        values.Device.RemoveAll(static entry => entry?.Value is null
                                                || string.IsNullOrWhiteSpace(entry.DeviceIdentityKey)
                                                || string.IsNullOrWhiteSpace(entry.CapabilityId)
                                                || entry.Value.Kind is CapabilityValueKind.None);
        foreach (var entry in values.Device)
        {
            entry.DeviceIdentityKey = entry.DeviceIdentityKey.Trim();
            entry.CapabilityId = entry.CapabilityId.Trim();
            entry.InstanceId = string.IsNullOrWhiteSpace(entry.InstanceId) ? null : entry.InstanceId.Trim();
            if (entry.Value is { Kind: CapabilityValueKind.Color, ColorValue: { } color })
            {
                // The picker hands back an alpha channel WSGM never uses, and a stored alpha byte reads
                // as a different colour when the value is unpacked as RGB.
                entry.Value = entry.Value with { ColorValue = color & 0xFFFFFF };
            }
        }

        // The resolver takes the first entry for a key, so a duplicate would decide the value by
        // file order.
        HashSet<(string, string, string?)> keys = [];
        values.Device.RemoveAll(entry =>
            !keys.Add((entry.DeviceIdentityKey, entry.CapabilityId, entry.InstanceId)));
    }

    private static DevicePowerPresetReference? NormalizePowerPreset(DevicePowerPresetReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        var pluginId = reference.PluginId?.Trim() ?? string.Empty;
        var presetId = reference.PresetId?.Trim() ?? string.Empty;
        if (pluginId.Length is 0 || presetId.Length is 0)
        {
            return null;
        }

        reference.PluginId = pluginId;
        reference.PresetId = presetId;
        if (presetId == "custom")
        {
            if (reference.CustomValues is not { SustainedWatts: > 0 } values
                || values.SlowWatts < values.SustainedWatts || !Enum.IsDefined(values.WindowsMode)
                || values.Scenario is { Length: 0 })
            {
                return null;
            }
        }
        else
        {
            reference.CustomValues = null;
        }

        return reference;
    }

    private static void NormalizeGameModeLaunch(GameModeLaunchConfiguration launch)
    {
        launch.Kind = Definite(launch.Kind, Defaults.GameModeLaunch.Kind);
        launch.Return = Definite(launch.Return, Defaults.GameModeLaunch.Return);
        launch.GameAudio = NormalizeAudioProfile(launch.GameAudio);
        launch.DesktopAudio = NormalizeAudioProfile(launch.DesktopAudio);
        launch.EnterActions = NormalizeSteps(launch.EnterActions);
        launch.LeaveActions = NormalizeSteps(launch.LeaveActions);
        launch.DesktopStartupActions = NormalizeSteps(launch.DesktopStartupActions);
        launch.DesktopWakeActions = NormalizeSteps(launch.DesktopWakeActions);
        launch.KnownDisplays ??= [];
        launch.KnownDisplays.RemoveAll(static display => display?.Target is null);
        foreach (var display in launch.KnownDisplays)
        {
            display.Modes =
            [
                .. (display.Modes ?? [])
                .Where(static mode => mode is { Width: > 0 and <= 16384, Height: > 0 and <= 16384 })
                .Distinct()
            ];
            display.MaximumDpiPercent = Math.Clamp(display.MaximumDpiPercent, 0, 500);
        }
    }

    private static AudioProfilePreference? NormalizeAudioProfile(AudioProfilePreference? profile)
    {
        if (profile is null)
        {
            return null;
        }

        profile.Output = NormalizeAudioEndpoint(profile.Output);
        profile.Input = NormalizeAudioEndpoint(profile.Input);
        profile.VolumePercent = profile.VolumePercent is { } volume and >= 0 and <= 100 ? volume : null;
        profile.PlaybackFormat = NormalizeAudioFormat(profile.PlaybackFormat);
        if (profile.Output is null)
        {
            profile.PlaybackFormat = null;
            profile.SpatialFormat = null;
        }

        return profile.Output is not null
               || profile.Input is not null
               || profile.VolumePercent is not null
               || profile.Muted is not null
               || profile.PlaybackFormat is not null
               || profile.SpatialFormat is not null
            ? profile
            : null;
    }

    private static AudioEndpointPreference? NormalizeAudioEndpoint(AudioEndpointPreference? endpoint)
    {
        if (endpoint?.Id is not { Length: > 0 and <= 512 } id)
        {
            return null;
        }

        endpoint.Id = id.Trim();
        if (endpoint.Id.Length == 0)
        {
            return null;
        }

        endpoint.Name = endpoint.Name?.Trim() is { Length: > 0 and <= 256 } name ? name : null;
        return endpoint;
    }

    private static AudioFormatPreference? NormalizeAudioFormat(AudioFormatPreference? format)
    {
        return format is
        {
            Channels: > 0 and <= 32,
            SampleRate: > 0 and <= 384000,
            BitsPerSample: > 0 and <= 32,
            ContainerBitsPerSample: > 0 and <= 32,
            ChannelMask: > 0
        } && format.BitsPerSample <= format.ContainerBitsPerSample
            ? format
            : null;
    }

    /// <summary>
    ///     Removes action steps that have no plugin or action identity, preserving explicit steps.
    /// </summary>
    private static List<PluginActionStep> NormalizeSteps(List<PluginActionStep>? steps)
    {
        steps ??= [];
        steps.RemoveAll(static step => step?.Plugin is not { } plugin
                                       || string.IsNullOrWhiteSpace(plugin.PluginId)
                                       || string.IsNullOrWhiteSpace(plugin.InstanceId)
                                       || string.IsNullOrWhiteSpace(step.ActionId));
        foreach (var step in steps)
        {
            step.Arguments ??= [];
            step.TimeoutSeconds = Math.Clamp(step.TimeoutSeconds, 1, 120);
        }

        return steps;
    }

    private static void NormalizeFilter(FilterNode node)
    {
        node.Kind = Definite(node.Kind, FilterDefaults.Kind);
        node.Mode = Definite(node.Mode, FilterDefaults.Mode);
        node.Condition = Definite(node.Condition, FilterDefaults.Condition);
        node.Platform = Definite(node.Platform, FilterDefaults.Platform);
        node.ScoreType = Definite(node.ScoreType, FilterDefaults.ScoreType);
        node.Units = Definite(node.Units, FilterDefaults.Units);
        node.CardScope = Definite(node.CardScope, FilterDefaults.CardScope);
        node.CollectionId ??= "";
        node.Pattern ??= "";
        node.ContentId ??= "";
        node.Children = [.. (node.Children ?? []).Where(static child => child is not null)];
        node.TagIds ??= [];
        node.AppIds ??= [];
        foreach (var child in node.Children)
        {
            NormalizeFilter(child);
        }
    }

    /// <summary>
    ///     Repairs explicit JSON nulls inside a splash section (see
    ///     <see cref="Normalize" />), bounds the display strings, and clamps every
    ///     numeric field into the range the Appearance editor enforces. Shared with
    ///     splash-theme import, which deserializes the same external contract from
    ///     archives.
    /// </summary>
    internal static SplashConfig NormalizeSplash(SplashConfig splash)
    {
        splash.Text ??= SplashFieldDefaults.Text;
        splash.TextColor ??= SplashFieldDefaults.TextColor;
        splash.Caption ??= SplashFieldDefaults.Caption;
        splash.CaptionColor ??= SplashFieldDefaults.CaptionColor;
        splash.SpinnerColor ??= SplashFieldDefaults.SpinnerColor;
        splash.BackgroundColor ??= SplashFieldDefaults.BackgroundColor;
        // "No image" has exactly one representation, "": every consumer tests these
        // with IsNullOrWhiteSpace, so a hand-edited config or an imported theme
        // carrying "   " means no image — and must not be persisted as whitespace by
        // the next save either (SplashAssets.PrepareSlot normalizes the same way).
        splash.BackgroundImagePath = Blank(splash.BackgroundImagePath);
        splash.LogoImagePath = Blank(splash.LogoImagePath);
        splash.TextPlacement ??= new SplashElementPlacement();
        splash.SpinnerPlacement ??= new SplashElementPlacement { Mode = SplashPlacementMode.WithText };
        splash.LogoPlacement ??= new SplashElementPlacement { Mode = SplashPlacementMode.WithText };

        splash.TitleFontSize = Math.Clamp(splash.TitleFontSize, MinFontSize, MaxTitleFontSize);
        splash.CaptionFontSize = Math.Clamp(splash.CaptionFontSize, MinFontSize, MaxCaptionFontSize);
        splash.SpinnerSize = Math.Clamp(splash.SpinnerSize, MinSpinnerSize, MaxSpinnerSize);
        splash.LogoMaxSize = Math.Clamp(splash.LogoMaxSize, MinLogoMaxSize, MaxLogoMaxSize);
        splash.SpinnerStyle = Definite(splash.SpinnerStyle, SplashFieldDefaults.SpinnerStyle);
        splash.SweepEdge = Definite(splash.SweepEdge, SplashFieldDefaults.SweepEdge);
        NormalizePlacement(splash.TextPlacement);
        NormalizePlacement(splash.SpinnerPlacement);
        NormalizePlacement(splash.LogoPlacement);
        return splash;
    }

    /// <summary>
    ///     Maps a null or whitespace-only image path to the single "no image"
    ///     value, leaving every real path untouched (leading/trailing spaces are legal
    ///     in Windows path components, so nothing else is trimmed).
    /// </summary>
    private static string Blank(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? "" : path;
    }

    /// <summary>
    ///     Clamps one element placement into the editor's ranges and drops
    ///     unknown enum members back to their defaults.
    /// </summary>
    private static void NormalizePlacement(SplashElementPlacement placement)
    {
        placement.Mode = Definite(placement.Mode, PlacementDefaults.Mode);
        placement.Anchor = Definite(placement.Anchor, PlacementDefaults.Anchor);
        placement.PaddingX = Math.Clamp(placement.PaddingX, MinPadding, MaxPadding);
        placement.PaddingY = Math.Clamp(placement.PaddingY, MinPadding, MaxPadding);
        placement.X = Math.Clamp(placement.X, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
        placement.Y = Math.Clamp(placement.Y, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
    }

}
