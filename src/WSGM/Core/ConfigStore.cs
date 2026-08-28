using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace WSGM.Core;

/// <summary>Loads and atomically saves WSGM's shared per-user configuration file.</summary>
public static class ConfigStore
{
    /// <summary>Absolute path of the persisted configuration file.</summary>
    public static string ConfigPath => Path.Combine(Log.Directory, "config.json");

    // Shell, settings window, and elevated one-shots all load-modify-save the same
    // file; the named mutex serializes the individual Load/Save calls so they never
    // interleave. It CANNOT merge: saving an AppConfig loaded long ago overwrites
    // every field another process persisted in between, so long-lived holders must
    // re-load and re-apply only their own fields before saving (see
    // SettingsViewModel.SaveMerged). The timeout is short and a miss only logs —
    // recovery paths must never block here.
    private const string MutexName = @"Local\WSGM.Config";
    private const int MutexTimeoutMs = 2000;

    /// <summary>Loads the current configuration, returning safe defaults when the
    /// file is absent, malformed, or inaccessible.</summary>
    /// <returns>A normalized configuration that callers can use without null checks.</returns>
    public static AppConfig Load()
    {
        using var guard = ConfigMutex.Acquire();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = DeserializeConfig(json);
                if (config is not null)
                {
                    return Normalize(config);
                }
            }
        }
        catch (Exception ex)
        {
            // The file holds the previous-shell/UAC/lock-screen registry snapshots;
            // set the corrupt file aside so they stay manually recoverable instead
            // of being clobbered when the next Save writes blank defaults.
            Log.Error("Failed to load config, using defaults", ex);
            PreserveCorruptFile();
        }
        return new AppConfig();
    }

    /// <summary>Loads configuration for a read-modify-write transaction. Unlike
    /// <see cref="Load"/>, an existing unreadable file is never converted to defaults:
    /// the exception aborts the mutation so registry recovery snapshots cannot be erased.</summary>
    /// <returns>The normalized configuration, or defaults only when no file exists.</returns>
    internal static AppConfig LoadForMutation()
    {
        using var guard = ConfigMutex.Acquire();
        if (!File.Exists(ConfigPath))
        {
            return new AppConfig();
        }
        var json = File.ReadAllText(ConfigPath);
        var config = DeserializeConfig(json)
            ?? throw new InvalidDataException("Configuration JSON contained no object.");
        return Normalize(config);
    }

    private static AppConfig? DeserializeConfig(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
        }
        catch (JsonException)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                ?? throw new JsonException("Configuration root was not an object.");
            RepairEnum(root, "GlyphStyle", GlyphStyle.Xbox);
            RepairEnum(root, "DisplayManagement", DisplayManagementMode.DpiOnly);
            if (root["Gestures"] is JsonObject gestures)
            {
                RepairEnum(gestures, "BottomEdgeAction", EdgeAction.Taskbar);
            }
            if (root["Splash"] is JsonObject splash)
            {
                RepairEnum(splash, "SpinnerStyle", SplashSpinnerStyle.Ring);
                RepairEnum(splash, "SweepEdge", SweepEdge.Bottom);
                RepairPlacement(splash["TextPlacement"] as JsonObject);
                RepairPlacement(splash["SpinnerPlacement"] as JsonObject);
                RepairPlacement(splash["LogoPlacement"] as JsonObject);
            }
            if (root["CustomTabs"] is JsonArray tabs)
            {
                foreach (var tab in tabs.OfType<JsonObject>())
                {
                    RepairFilterJson(tab["FilterTree"] as JsonObject);
                }
            }
            if (root["LaunchWrappers"] is JsonArray wrappers)
            {
                foreach (var wrapper in wrappers.OfType<JsonObject>())
                {
                    RepairEnum(wrapper, "Mode", LaunchWrapperMode.None);
                    RepairEnum(wrapper, "Kind", LaunchConfigurationKind.Wrapper);
                }
            }
            return JsonSerializer.Deserialize(root.ToJsonString(), ConfigJsonContext.Default.AppConfig);
        }
    }

    private static void RepairPlacement(JsonObject? placement)
    {
        if (placement is null) { return; }
        RepairEnum(placement, "Mode", SplashPlacementMode.Anchor);
        RepairEnum(placement, "Anchor", SplashPlacementAnchor.Center);
    }

    private static void RepairFilterJson(JsonObject? filter)
    {
        if (filter is null) { return; }
        RepairEnum(filter, "Kind", FilterKind.Installed);
        RepairEnum(filter, "Mode", FilterMode.And);
        RepairEnum(filter, "Condition", ThresholdCondition.Above);
        RepairEnum(filter, "Platform", PlatformKind.Steam);
        RepairEnum(filter, "ScoreType", ReviewScoreType.SteamPercent);
        RepairEnum(filter, "Units", TimeUnit.Hours);
        RepairEnum(filter, "CardScope", SdCardScope.Inserted);
        if (filter["Children"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
            {
                RepairFilterJson(child);
            }
        }
    }

    private static void RepairEnum<T>(JsonObject value, string property, T fallback)
        where T : struct, Enum
    {
        if (value[property] is JsonValue node
            && node.TryGetValue<string>(out var text)
            && !Enum.TryParse<T>(text, ignoreCase: true, out _))
        {
            value[property] = fallback.ToString();
        }
    }

    /// <summary>An explicit JSON null ("StartupApps": null) deserializes over the
    /// property initializer; replace nulls with fresh defaults so a hand-edited
    /// config can never NRE the shell later (which would kill it before the panic
    /// handler runs). New nested object/list members belong in this list too.</summary>
    internal static AppConfig Normalize(AppConfig config)
    {
        if (!Enum.IsDefined(config.DisplayManagement))
        {
            config.DisplayManagement = DisplayManagementMode.DpiOnly;
        }
        config.StartupApps ??= [];
        config.DeviceIntegration ??= new DeviceIntegrationConfig();
        NormalizeDeviceIntegration(config.DeviceIntegration);
        config.Cef ??= new CefConfig();
        config.Hotkey ??= new HotkeyConfig();
        config.GamepadChord ??= new GamepadChordConfig();
        config.Gestures ??= new GestureConfig();
        config.SavedDisplayScales ??= [];
        config.SavedDisplayScaleEntries ??= [];
        config.DisplayProfiles ??= [];
        config.PreviousConsoleLockSchemeValues ??= [];
        config.CardLibraries ??= [];
        config.ForgottenInsertedCardIds ??= [];
        config.ForgottenInsertedCardIds = config.ForgottenInsertedCardIds
            .Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        config.CategoryTabs ??= [];
        config.CustomTabs ??= [];
        config.LibraryTabOrder ??= [];
        config.HiddenNativeTabs ??= [];
        config.KnownNativeTabs ??= [];
        config.SteamGridDbApiKey ??= "";
        config.SgdbLinks ??= [];
        config.LaunchWrappers ??= [];
        // A null ELEMENT ("StartupApps": [null]) survives the list-level ??= above and
        // would NRE in SelfElevation before the crash-loop breaker has recorded the
        // start — the shell would then die at every sign-in with nothing disarming it.
        // RemoveAll repairs in place: Normalize must hand back the caller's own list
        // instances (RegressionCoverageTests pins that), and rebuilding them would
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
        config.CardLibraries = config.CardLibraries.Where(static card => card is not null).ToList();
        foreach (var card in config.CardLibraries)
        {
            card.ContentId ??= "";
            card.Name ??= "";
            card.AppIds ??= [];
            card.CollectionId ??= "";
            card.LastLetter ??= "";
        }
        config.CategoryTabs = config.CategoryTabs.Where(static category => category is not null).ToList();
        foreach (var category in config.CategoryTabs)
        {
            category.Name ??= "";
            category.CollectionId ??= "";
        }
        config.CustomTabs = config.CustomTabs.Where(static tab => tab is not null).ToList();
        foreach (var tab in config.CustomTabs)
        {
            tab.Id = string.IsNullOrWhiteSpace(tab.Id) ? Guid.NewGuid().ToString("N") : tab.Id;
            tab.Name ??= "";
            tab.CollectionId ??= "";
            tab.FilterTree ??= new FilterNode { Kind = FilterKind.Merge };
            NormalizeFilter(tab.FilterTree);
        }
        config.LibraryTabOrder = config.LibraryTabOrder
            .Where(static key => key is not null).ToList();
        config.HiddenNativeTabs = config.HiddenNativeTabs
            .Where(static id => id is not null).ToList();
        config.KnownNativeTabs = config.KnownNativeTabs
            .Where(static tab => tab is not null).ToList();
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
        config.DisplayProfiles.RemoveAll(static profile => profile is null);
        foreach (var profile in config.DisplayProfiles)
        {
            profile.MonitorId ??= "";
            profile.DeviceName ??= "";
            profile.DisplayName ??= "";
            profile.Desktop ??= new DisplayModeValues();
            profile.Game ??= new DisplayModeValues();
            NormalizeDisplayMode(profile.Desktop);
            NormalizeDisplayMode(profile.Game);
        }
        config.PreviousConsoleLockSchemeValues.RemoveAll(static scheme => scheme is null);
        foreach (var scheme in config.PreviousConsoleLockSchemeValues)
        {
            scheme.SchemeGuid ??= "";
        }
        config.SgdbLinks.RemoveAll(static link => link is null);
        foreach (var link in config.SgdbLinks)
        {
            link.Name ??= "";
        }
        config.AccentColor ??= "#FFFF9D3D";
        config.AccentColor = Truncate(config.AccentColor, MaxColorLength, "Accent color");
        config.Splash ??= new SplashConfig();
        NormalizeSplash(config.Splash);
        return config;
    }

    private static void NormalizeDeviceIntegration(DeviceIntegrationConfig device)
    {
        if (!Enum.IsDefined(device.UpdatePolicy))
        {
            device.UpdatePolicy = DevicePluginUpdatePolicy.Notify;
        }

        if (!Enum.IsDefined(device.ControllerTarget))
        {
            device.ControllerTarget = ManagedControllerTarget.SteamDeckComposite;
        }

        if (!Enum.IsDefined(device.GlyphSelection))
        {
            device.GlyphSelection = DeviceGlyphSelection.Automatic;
        }

        if (!Enum.IsDefined(device.DiagnosticLevel))
        {
            device.DiagnosticLevel = DeviceDiagnosticLevel.Standard;
        }

        device.ManualGlyphProfileId = string.IsNullOrWhiteSpace(device.ManualGlyphProfileId)
            ? null
            : device.ManualGlyphProfileId.Trim();
        device.PackageSelections ??= [];
        device.Profiles ??= [];
        device.PackageSelections.RemoveAll(static selection => selection is null
            || string.IsNullOrWhiteSpace(selection.DeviceIdentityKey)
            || string.IsNullOrWhiteSpace(selection.PackageId));
        foreach (DevicePackageSelection selection in device.PackageSelections)
        {
            selection.DeviceIdentityKey = selection.DeviceIdentityKey.Trim();
            selection.PackageId = selection.PackageId.Trim();
            selection.Version = string.IsNullOrWhiteSpace(selection.Version)
                ? null
                : selection.Version.Trim();
        }

        device.Profiles.RemoveAll(static profile => profile is null
            || string.IsNullOrWhiteSpace(profile.DeviceIdentityKey));
        foreach (DeviceDesiredProfile profile in device.Profiles)
        {
            profile.DeviceIdentityKey = profile.DeviceIdentityKey.Trim();
            profile.SelectedHardwareProfileId = string.IsNullOrWhiteSpace(profile.SelectedHardwareProfileId)
                ? null
                : profile.SelectedHardwareProfileId.Trim();
            profile.Capabilities ??= [];
            profile.OemAssignments ??= [];
            profile.ControllerTargets ??= [];
            profile.Capabilities.RemoveAll(static capability => capability is null
                || string.IsNullOrWhiteSpace(capability.CapabilityId));
            foreach (DeviceCapabilityPreference capability in profile.Capabilities)
            {
                capability.CapabilityId = capability.CapabilityId.Trim();
                capability.InstanceId = string.IsNullOrWhiteSpace(capability.InstanceId)
                    ? null
                    : capability.InstanceId.Trim();
                capability.HardwareProfiles ??= [];
                capability.ApplicationOverrides ??= [];
                capability.HardwareProfiles.RemoveAll(static value => value is null
                    || string.IsNullOrWhiteSpace(value.ProfileId));
                capability.ApplicationOverrides.RemoveAll(static value => value is null
                    || string.IsNullOrWhiteSpace(value.ApplicationId));
            }

            profile.OemAssignments.RemoveAll(static assignment => assignment is null
                || string.IsNullOrWhiteSpace(assignment.ControlId)
                || !Enum.IsDefined(assignment.Action));
            profile.ControllerTargets.RemoveAll(static target => target is null
                || string.IsNullOrWhiteSpace(target.ApplicationId)
                || !Enum.IsDefined(target.Target));
        }
    }

    private static void NormalizeDisplayMode(DisplayModeValues mode)
    {
        mode.Width = Math.Clamp(mode.Width, 0, 16384);
        mode.Height = Math.Clamp(mode.Height, 0, 16384);
        mode.RefreshRate = Math.Clamp(mode.RefreshRate, 0, 1000);
        mode.DpiPercent = DisplayScale.NormalizeConfiguredPercent(Math.Clamp(mode.DpiPercent, 100, 500));
    }

    private static void NormalizeFilter(FilterNode node)
    {
        if (!Enum.IsDefined(node.Kind)) { node.Kind = FilterKind.Installed; }
        if (!Enum.IsDefined(node.Mode)) { node.Mode = FilterMode.And; }
        if (!Enum.IsDefined(node.Condition)) { node.Condition = ThresholdCondition.Above; }
        if (!Enum.IsDefined(node.Platform)) { node.Platform = PlatformKind.Steam; }
        if (!Enum.IsDefined(node.ScoreType)) { node.ScoreType = ReviewScoreType.SteamPercent; }
        if (!Enum.IsDefined(node.Units)) { node.Units = TimeUnit.Hours; }
        if (!Enum.IsDefined(node.CardScope)) { node.CardScope = SdCardScope.Inserted; }
        node.CollectionId ??= "";
        node.Pattern ??= "";
        node.ContentId ??= "";
        node.Children = (node.Children ?? []).Where(static child => child is not null).ToList();
        node.TagIds ??= [];
        node.AppIds ??= [];
        foreach (var child in node.Children)
        {
            NormalizeFilter(child);
        }
    }

    // Bounds for every numeric splash field, mirrored 1:1 from the Appearance
    // editor's NumericUpDown Minimum/Maximum values (Settings\Pages\AppearancePage.axaml).
    // The editor can never produce anything outside them — but a shared .wsgmsplash
    // theme and a hand-edited config.json can, and the splash renderer only
    // lower-bounds its inputs, so "SpinnerSize": 2147483647 would explode layout
    // before the boot cover is usable. Clamping here covers BOTH untrusted paths:
    // config load (via Normalize) and theme import (SplashTheme.Import).
    private const int MinFontSize = 1;
    private const int MaxTitleFontSize = 400;
    private const int MaxCaptionFontSize = 200;
    private const int MinSpinnerSize = 1;
    private const int MaxSpinnerSize = 1024;
    private const int MinLogoMaxSize = 1;
    private const int MaxLogoMaxSize = 4096;
    private const int MinPadding = 0;
    private const int MaxPadding = 4096;
    private const int MinAbsoluteCoordinate = 0;
    private const int MaxAbsoluteCoordinate = 16384;

    // Length caps for the splash STRINGS, for the same reason the numbers above are
    // clamped — except the damage here is layout cost, not a bad value. The title
    // and the caption are each rendered as ONE unwrapped TextBlock line
    // (Shell\BootSplashWindow sets no TextWrapping) and are bound straight into the
    // Appearance text boxes on import. A shared .wsgmsplash may spend nearly its
    // whole 1 MiB splash.json allowance on one of those strings — a trivially small
    // archive once compressed — and Avalonia would then lay out hundreds of
    // thousands of glyphs in a single run: first in Settings when the theme is
    // imported, then in the boot splash on every following sign-in.
    //
    // 200 characters cannot plausibly cut a real splash line: a title is a few
    // words ("Please wait", "Starting Steam Big Picture…"), and even at the default
    // 26 px title size only ~130 characters fit across a 1080p panel before the
    // single line runs off screen, so anything approaching the cap is already
    // unreadable by design.
    private const int MaxSplashTextLength = 200;

    // Color strings are shown verbatim in the Appearance hex boxes and parsed by
    // Shell\SplashStyle.ParseColor (splash) or Avalonia's Color.TryParse (accent).
    // The longest value that can ever parse is "#AARRGGBB" (9 characters) or
    // Avalonia's longest known-color name, "LightGoldenrodYellow" (20); 32 keeps
    // every real value with room to spare.
    //
    // The accent color has exactly the same shape as the splash ones — hand-editable
    // in config.json, bound to a TextBox, and re-parsed over its WHOLE length on every
    // keystroke (Settings\Pages\AppearancePage re-parses it on each PropertyChanged to
    // repaint the swatches and the picker) — so it is bounded here too. Without the
    // cap a 1 MiB value survived Normalize and made every keystroke in that box parse
    // a megabyte.
    private const int MaxColorLength = 32;

    /// <summary>Repairs explicit JSON nulls inside a splash section (see
    /// <see cref="Normalize"/>), bounds the display strings, and clamps every
    /// numeric field into the range the Appearance editor enforces. Shared with
    /// splash-theme import, which deserializes the same contract from untrusted
    /// archives.</summary>
    internal static SplashConfig NormalizeSplash(SplashConfig splash)
    {
        splash.Text ??= "Please wait";
        splash.TextColor ??= "#FFFFFF";
        splash.Caption ??= "";
        splash.CaptionColor ??= "#666666";
        splash.SpinnerColor ??= "#FFFFFF";
        splash.BackgroundColor ??= "#000000";
        // Truncate rather than reject: a theme whose title is too long is still a
        // usable theme, and dropping the whole import over one field would lose the
        // images and every other setting with it.
        splash.Text = Truncate(splash.Text, MaxSplashTextLength, "Splash title text");
        splash.Caption = Truncate(splash.Caption, MaxSplashTextLength, "Splash caption");
        splash.TextColor = Truncate(splash.TextColor, MaxColorLength, "Splash text color");
        splash.CaptionColor = Truncate(splash.CaptionColor, MaxColorLength, "Splash caption color");
        splash.SpinnerColor = Truncate(splash.SpinnerColor, MaxColorLength, "Splash spinner color");
        splash.BackgroundColor = Truncate(splash.BackgroundColor, MaxColorLength, "Splash background color");
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
        // A JSON number ("SpinnerStyle": 999) deserializes into the enum unchecked;
        // an unknown member falls back to the field's default rather than to whatever
        // neighbouring style a clamp would land on.
        if ((int)splash.SpinnerStyle is < 0 or > (int)SplashSpinnerStyle.Off)
        {
            splash.SpinnerStyle = SplashSpinnerStyle.Ring;
        }
        if ((int)splash.SweepEdge is < 0 or > (int)SweepEdge.Top)
        {
            splash.SweepEdge = SweepEdge.Bottom;
        }
        NormalizePlacement(splash.TextPlacement);
        NormalizePlacement(splash.SpinnerPlacement);
        NormalizePlacement(splash.LogoPlacement);
        return splash;
    }

    /// <summary>Cuts an over-long display string down to <paramref name="limit"/>
    /// characters, logging once with the original length so a truncated shared theme
    /// (or a hand-edited config) is diagnosable from the log. Values within the limit
    /// are returned untouched — no trimming, no other rewriting.</summary>
    /// <param name="value">The value to bound.</param>
    /// <param name="limit">Maximum number of characters to keep.</param>
    /// <param name="field">Human-readable field label for the warning, written as it
    /// should read at the start of the sentence ("Splash caption", "Accent color").</param>
    private static string Truncate(string value, int limit, string field)
    {
        if (value.Length <= limit)
        {
            return value;
        }
        // Never cut between the halves of a surrogate pair — a lone surrogate would
        // render as a replacement glyph at the end of an otherwise fine line.
        var keep = char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
        Log.Warn($"{field} is {value.Length} characters — truncated to {keep}.");
        return value[..keep];
    }

    /// <summary>Maps a null or whitespace-only image path to the single "no image"
    /// value, leaving every real path untouched (leading/trailing spaces are legal
    /// in Windows path components, so nothing else is trimmed).</summary>
    private static string Blank(string? path) => string.IsNullOrWhiteSpace(path) ? "" : path;

    /// <summary>Clamps one element placement into the editor's ranges and drops
    /// unknown enum members back to their defaults.</summary>
    private static void NormalizePlacement(SplashElementPlacement placement)
    {
        if ((int)placement.Mode is < 0 or > (int)SplashPlacementMode.WithText)
        {
            placement.Mode = SplashPlacementMode.Anchor;
        }
        if ((int)placement.Anchor is < 0 or > (int)SplashPlacementAnchor.BottomRight)
        {
            placement.Anchor = SplashPlacementAnchor.Center;
        }
        placement.PaddingX = Math.Clamp(placement.PaddingX, MinPadding, MaxPadding);
        placement.PaddingY = Math.Clamp(placement.PaddingY, MinPadding, MaxPadding);
        // The editor's absolute X/Y spinners start at 0 (an element placed off the
        // top-left is unreachable, not a feature), so negatives clamp up to 0 here.
        placement.X = Math.Clamp(placement.X, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
        placement.Y = Math.Clamp(placement.Y, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
    }

    private static void PreserveCorruptFile()
    {
        try
        {
            // This runs in the elevated one-shots too (UacSettings/LockScreenSettings
            // call Load), and %LOCALAPPDATA%\WSGM is writable by the unelevated user:
            // a pre-planted reparse point at a PREDICTABLE destination would redirect
            // an overwriting elevated copy (CopyFileEx follows destination links). An
            // unpredictable name cannot be pre-planted, and CreateNew refuses to write
            // through anything that already occupies it — no overwrite, no follow.
            var bad = Path.Combine(Log.Directory, $"config.bad.{Guid.NewGuid():N}.json");
            using (var source = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dest = new FileStream(bad, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(dest);
            }
            Log.Error($"Corrupt config preserved at {bad} — registry snapshots may be recoverable from it.");
            PruneCorruptFiles();
        }
        catch
        {
            // Best effort — an unreadable file cannot be preserved either.
        }
    }

    /// <summary>Keeps only the newest few preserved copies. Every Load of a broken
    /// config writes another uniquely named one — several per boot across the shell,
    /// Settings and the elevated one-shots — and nothing else ever reclaims them.
    /// Deleting by enumerated exact name keeps the unpredictable-name property that
    /// makes the write itself reparse-point safe.</summary>
    private static void PruneCorruptFiles()
    {
        const int keep = 5;
        try
        {
            var stale = new DirectoryInfo(Log.Directory)
                .GetFiles("config.bad.*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep);
            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not prune {file.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not prune preserved configs: {ex.Message}");
        }
    }

    /// <summary>Atomically persists a complete configuration snapshot.</summary>
    /// <param name="config">The configuration state to serialize.</param>
    public static void Save(AppConfig config)
    {
        using var guard = ConfigMutex.Acquire();
        Directory.CreateDirectory(Log.Directory);
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        // Per-process temp name so concurrent savers never share it; a leftover
        // .tmp from a failed rename is harmlessly overwritten by that process later.
        var temp = $"{ConfigPath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, json);
        // Atomic replace (MoveFileEx REPLACE_EXISTING) — covers both the exists and
        // not-yet-exists cases without a TOCTOU window.
        File.Move(temp, ConfigPath, overwrite: true);
    }

    /// <summary>The only supported read-modify-write path for config.json: takes the
    /// cross-process lock, loads through <see cref="LoadForMutation"/>, applies
    /// <paramref name="mutate"/>, and saves — all inside one scope, so no other WSGM
    /// process can persist between the read and the write and have its fields dropped
    /// by it. Callers must apply ONLY their own fields: everything else in the loaded
    /// instance is written straight back.
    /// <para>The strict load is the point. <see cref="Load"/> answers an unreadable
    /// file with defaults, which is right for a reader but catastrophic here — saving
    /// those defaults erases the previous-shell/UAC/lock-screen registry snapshots
    /// uninstall restores from. An unreadable existing file therefore throws out of
    /// this method and ABORTS the mutation; <see cref="Load"/> stays available for
    /// read-only callers.</para>
    /// <para>A caller that needs more work under the same lock (see
    /// SettingsViewModel.SaveMerged, which also promotes splash assets and writes the
    /// boot manifest) wraps this in its own <see cref="AcquireLock"/> scope — the
    /// nested acquisition is free.</para></summary>
    /// <param name="mutate">Applies the caller's fields to the freshly loaded configuration.</param>
    /// <returns>The configuration instance that was persisted.</returns>
    /// <exception cref="InvalidDataException">The existing file could not be parsed.</exception>
    internal static AppConfig Mutate(Action<AppConfig> mutate)
    {
        using var guard = ConfigMutex.Acquire();
        var config = LoadForMutation();
        mutate(config);
        Save(config);
        return config;
    }

    /// <summary>Takes the cross-process config lock for a caller that must keep a
    /// whole read-modify-write sequence — plus the file work between its steps —
    /// atomic against other WSGM processes. SettingsViewModel.SaveMerged holds it
    /// across Load → Save → the splash-asset Commit → the boot-manifest write, so
    /// config.json and the live splash images can never be left describing different
    /// states. Only FAST operations belong in such a scope: the timeout below is
    /// sized for a small JSON write, so anything slow (the splash-asset staging
    /// copies, which can be tens of megabytes) must be done before the lock is taken.
    /// <para>The <see cref="Load"/> and <see cref="Save"/> calls made inside such a
    /// scope acquire the SAME lock again. Those nested acquisitions are FREE: a
    /// thread-local depth counter short-circuits them, so they neither touch the
    /// kernel object nor — and this is the point — pay the
    /// <see cref="MutexTimeoutMs"/> timeout a second, third and fourth time when
    /// another process holds the lock. Relying on the Win32 mutex's own per-thread
    /// recursion count instead made a contended save cost one timeout per nested call
    /// (Load + Save + repair Save + the outer scope ≈ 6-8 s of frozen UI and four
    /// "Config mutex timed out" lines). Only the OUTERMOST scope releases, so the hold
    /// survives until this scope is disposed, and the degraded no-lock path is
    /// inherited by the nested calls — acquiring the lock for one step of a sequence
    /// whose outer scope already gave up would not restore any guarantee.</para></summary>
    /// <returns>A scope that releases the lock when disposed.</returns>
    internal static IDisposable AcquireLock() => ConfigMutex.Acquire();

    /// <summary>Test seam: how deeply the CALLING thread currently holds the config
    /// lock (0 = not held). Exists so the acquire/release balance of the nested scopes
    /// can be asserted without going near the per-user config file.</summary>
    internal static int LockDepth => ConfigMutex.CurrentDepth;

    /// <summary>Whether the calling thread owns the named mutex rather than using
    /// the recovery-only degraded path.</summary>
    internal static bool HasExclusiveLock => ConfigMutex.HasExclusiveOwnership;

    /// <summary>Cross-process guard around Load/Save. Failure to create or acquire
    /// the mutex degrades to lock-less operation with a warning — never a deadlock.
    /// Re-entrant per thread through a depth counter: only the outermost scope talks
    /// to the kernel object, so a nested acquisition costs nothing even while another
    /// process holds the lock.
    /// <para>Scopes are meant to be disposed in reverse acquisition order (they are
    /// all <c>using</c> blocks today). Out-of-order disposal is a caller error, and
    /// what is guaranteed for it is only that the state stays sound: the depth never
    /// goes negative, a late nested Dispose cannot pop a level it does not own, and
    /// the mutex is released exactly once — by the scope that acquired it, at the
    /// moment that scope is disposed. Cross-process exclusion consequently ENDS
    /// there: a nested scope that outlives its owner holds nothing, and the counter
    /// stops pretending otherwise rather than blocking a later real acquisition.</para></summary>
    private sealed class ConfigMutex : IDisposable
    {
        // Per-thread lock state. The mutex itself is thread-owned in Win32, so the
        // depth can only ever describe the thread that took it; a nested acquisition
        // from ANOTHER thread is a real, competing acquisition and is treated as one.
        [ThreadStatic]
        private static int _depth;
        [ThreadStatic]
        private static bool _hasExclusiveOwnership;

        private readonly Mutex? _mutex;
        private readonly bool _owned;
        private readonly bool _nested;

        // The depth this scope established (1 for the outermost). Dispose pops back
        // to _level - 1 instead of blindly decrementing, which is what keeps the
        // counter sane when scopes are disposed OUT OF ORDER (see Dispose).
        private readonly int _level;
        private bool _disposed;

        private ConfigMutex(Mutex? mutex, bool owned, bool nested, int level)
        {
            _mutex = mutex;
            _owned = owned;
            _nested = nested;
            _level = level;
        }

        /// <summary>How deeply the calling thread holds the lock (0 = not at all).</summary>
        internal static int CurrentDepth => _depth;
        internal static bool HasExclusiveOwnership => _hasExclusiveOwnership;

        public static ConfigMutex Acquire()
        {
            if (_depth > 0)
            {
                // Already held by this thread (SaveMerged's scope around Load/Save):
                // no kernel call, and above all no second MutexTimeoutMs wait.
                _depth++;
                return new ConfigMutex(null, owned: false, nested: true, level: _depth);
            }

            Mutex? mutex = null;
            var owned = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, MutexName);
                try
                {
                    owned = mutex.WaitOne(MutexTimeoutMs);
                }
                catch (AbandonedMutexException)
                {
                    // Previous holder died mid-section; Save is atomic, the file is intact.
                    // The wait DID succeed, so this scope owns the mutex and must release it.
                    owned = true;
                }
                if (!owned)
                {
                    Log.Warn("Config mutex timed out — proceeding without cross-process lock.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Config mutex unavailable, proceeding without lock: {ex.Message}");
            }
            // Counted even when the acquisition degraded, so the nested steps of one
            // sequence inherit that decision instead of each paying the timeout again.
            _depth = 1;
            _hasExclusiveOwnership = owned;
            return new ConfigMutex(mutex, owned, nested: false, level: 1);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                // Balance is per scope: a double Dispose (an explicit one plus the
                // `using`) must not pop a depth level its scope never pushed.
                return;
            }
            _disposed = true;

            // Pop to this scope's own level rather than decrementing blindly. Scopes
            // are meant to nest strictly (`using` blocks), but disposing them OUT OF
            // ORDER used to drive the counter negative — the outermost scope assigned
            // 0 while a nested one was still live, and that nested Dispose then made
            // it -1, after which the next Acquire on this thread took the slow kernel
            // path even though the lock was free, and a later nested scope could pop
            // an unrelated real acquisition. The guard covers both directions:
            //   • depth still at or above this level → this scope is the deepest one
            //     that is still counted, so its level - 1 is the correct new depth;
            //   • depth already BELOW it → an outer scope was disposed first and has
            //     reset the counter (possibly for a fresh acquisition since), so this
            //     late Dispose must not touch it at all.
            if (_depth >= _level)
            {
                _depth = _level - 1;
                if (_depth == 0)
                {
                    _hasExclusiveOwnership = false;
                }
            }

            if (_nested)
            {
                // Nested scopes never touch the kernel object; only the scope that
                // acquired the mutex releases it, exactly once.
                return;
            }

            try
            {
                if (_owned)
                {
                    _mutex?.ReleaseMutex();
                }
            }
            catch (Exception ex)
            {
                // Never let lock cleanup break a save/load path — but never let it skip
                // the handle either: a swallowed ReleaseMutex failure used to leave the
                // Mutex undisposed AND the named object owned, after which every other
                // WSGM process ran the degraded lock-less path for the rest of the
                // session. Closing the handle abandons the mutex instead, which the
                // next waiter gets (as AbandonedMutexException) immediately.
                Log.Warn($"Config mutex release failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    _mutex?.Dispose();
                }
                catch
                {
                    // Closing a handle: nothing left to fall back to.
                }
            }
        }
    }
}
