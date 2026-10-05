using System;
using System.Linq;
using WSGM.Core;

namespace WSGM.Settings;

/// <summary>What a Settings save changed in the persisted configuration, and so has to apply outside it.</summary>
/// <param name="Shim">Steam Input management changed, so the shim in Steam's directory is reconciled.</param>
/// <param name="SteamAutostartAccepted">The Steam autostart takeover was accepted by this save.</param>
/// <param name="OtherManagersAccepted">The other-managers takeover was accepted by this save.</param>
internal sealed record SaveChanges(bool Shim, bool SteamAutostartAccepted, bool OtherManagersAccepted);

/// <summary>Applies a captured Settings edit to the fresh strict configuration load.</summary>
internal static class SettingsSaveMerge
{
    /// <summary>Copies the Settings-owned fields of a captured edit onto the fresh load.</summary>
    /// <param name="fresh">The strict load inside the writer transaction; mutated and returned.</param>
    /// <param name="request">The values this window captured.</param>
    /// <param name="preparedSplash">The splash section whose images were already staged.</param>
    /// <returns>
    ///     The merged configuration and what it changed against the disk value, measured here so every
    ///     persistence path, production or test, reports the same answer.
    /// </returns>
    internal static (AppConfig Config, SaveChanges Changes) Apply(AppConfig fresh, SettingsViewModel.SaveRequest request,
        SplashConfig preparedSplash)
    {
        var config = fresh;
        var values = ConfigJson.Clone(request.Values, ConfigJsonContext.Tolerant.AppConfig);
        var discoveredDisplays = fresh.GameModeLaunch.KnownDisplays;
        var shimWas = fresh.SteamInputManagementEnabled;
        var autostartWas = fresh.SteamAutostartTakeoverAccepted;
        var managersWas = fresh.OtherManagersTakeoverAccepted;
        config.SteamAutoRelaunch = values.SteamAutoRelaunch;
        config.SteamLaunchUnelevated = values.SteamLaunchUnelevated;
        config.StartupDelayMs = values.StartupDelayMs;
        config.StaggerDelayMs = values.StaggerDelayMs;
        config.BootSplashEnabled = values.BootSplashEnabled;
        config.SteamAutostartTakeoverAccepted = values.SteamAutostartTakeoverAccepted;
        config.OtherManagersTakeoverAccepted = values.OtherManagersTakeoverAccepted;
        config.MuteWhileDisplayOff = values.MuteWhileDisplayOff;
        config.CheckForUpdates = values.CheckForUpdates;
        config.ResuspendUnexplainedWakes = values.ResuspendUnexplainedWakes;
        config.LogVerbosity = values.LogVerbosity;
        config.AutoTdpTraceEnabled = values.AutoTdpTraceEnabled;
        config.Hotkey = values.Hotkey;
        config.GamepadChord = values.GamepadChord;
        config.GlyphStyle = values.GlyphStyle;
        config.AccentColor = values.AccentColor;
        config.OverlayBlurRadius = values.OverlayBlurRadius;
        config.StartupApps = values.StartupApps;
        config.GameModeLaunch.Kind = values.GameModeLaunch.Kind;
        config.GameModeLaunch.Return = values.GameModeLaunch.Return;
        config.GameModeLaunch.GameLayout = values.GameModeLaunch.GameLayout;
        config.GameModeLaunch.DesktopLayout = values.GameModeLaunch.DesktopLayout;
        config.GameModeLaunch.GameAudio = values.GameModeLaunch.GameAudio;
        config.GameModeLaunch.DesktopAudio = values.GameModeLaunch.DesktopAudio;
        config.GameModeLaunch.WaitForDisplay = values.GameModeLaunch.WaitForDisplay;
        config.GameModeLaunch.EnterActions = values.GameModeLaunch.EnterActions;
        config.GameModeLaunch.LeaveActions = values.GameModeLaunch.LeaveActions;
        config.GameModeLaunch.DesktopStartupActions = values.GameModeLaunch.DesktopStartupActions;
        config.GameModeLaunch.DesktopWakeActions = values.GameModeLaunch.DesktopWakeActions;
        config.GameModeLaunch.KnownDisplays = values.GameModeLaunch.KnownDisplays;
        config.Artwork.SteamGridDbApiKey = values.Artwork.SteamGridDbApiKey;
        config.Artwork.ScreenscraperEnabled = values.Artwork.ScreenscraperEnabled;
        config.Artwork.ScreenscraperUser = values.Artwork.ScreenscraperUser;
        config.Artwork.ScreenscraperUserPassword = values.Artwork.ScreenscraperUserPassword;
        config.Artwork.TabOrder = values.Artwork.TabOrder;
        config.Artwork.DefaultTab = values.Artwork.DefaultTab;
        config.Artwork.ShowGrid = values.Artwork.ShowGrid;
        config.Artwork.ShowWide = values.Artwork.ShowWide;
        config.Artwork.ShowHero = values.Artwork.ShowHero;
        config.Artwork.ShowLogo = values.Artwork.ShowLogo;
        config.Artwork.ShowIcon = values.Artwork.ShowIcon;
        config.Artwork.ShowManage = values.Artwork.ShowManage;
        config.GameLibrary.DefaultMode = values.GameLibrary.DefaultMode;
        config.GameLibrary.ImportUnroutable = values.GameLibrary.ImportUnroutable;
        config.GameLibrary.ArtworkPreference = values.GameLibrary.ArtworkPreference;
        config.DeviceIntegration.ControllerManagementEnabled = values.DeviceIntegration.ControllerManagementEnabled;
        config.DeviceIntegration.KeepGuideChordEdits = values.DeviceIntegration.KeepGuideChordEdits;
        config.Performance.Enabled = values.Performance.Enabled;
        config.Performance.FrameLimitStrategy = values.Performance.FrameLimitStrategy;
        config.Performance.OsdCustomOrder = values.Performance.OsdCustomOrder;
        config.Performance.OsdCustomTime = values.Performance.OsdCustomTime;
        config.Performance.OsdCustomFps = values.Performance.OsdCustomFps;
        config.Performance.OsdCustomCpu = values.Performance.OsdCustomCpu;
        config.Performance.OsdCustomRam = values.Performance.OsdCustomRam;
        config.Performance.OsdCustomGpu = values.Performance.OsdCustomGpu;
        config.Performance.OsdCustomVram = values.Performance.OsdCustomVram;
        config.Performance.OsdCustomBattery = values.Performance.OsdCustomBattery;
        config.Gestures.BottomEdge = values.Gestures.BottomEdge;
        config.Gestures.TopEdge = values.Gestures.TopEdge;
        config.Gestures.LeftEdgeSteamMenu = values.Gestures.LeftEdgeSteamMenu;
        config.Gestures.RightEdgeSteamQuickAccess = values.Gestures.RightEdgeSteamQuickAccess;

        foreach (var field in WsgmSharedSettings.All.Where(field => request.SharedEdits.Contains(field.Name)))
        {
            field.Write(config, field.Read(values));
        }

        foreach (var edit in request.CommonPluginEdits)
        {
            config.PluginInstances.RemoveAll(entry =>
                entry.PluginId == edit.PluginId && entry.InstanceId == edit.InstanceId);
            config.PluginInstances.Add(new CommonPluginInstanceConfig
                { PluginId = edit.PluginId, InstanceId = edit.InstanceId, Enabled = edit.Enabled });
        }

        // Preserve display facts discovered since the editor opened without resurrecting displays
        // the user explicitly forgot.
        foreach (var discovered in discoveredDisplays)
        {
            if (discovered.Target is not { } identity ||
                request.ForgottenDisplays.Any(target => target.Matches(identity)))
            {
                continue;
            }

            var edited = config.GameModeLaunch.KnownDisplays.FirstOrDefault(display =>
                display.Target?.Matches(identity) is true);
            if (edited is null)
            {
                config.GameModeLaunch.KnownDisplays.Add(discovered);
            }
            else
            {
                edited.Modes = [.. edited.Modes.Concat(discovered.Modes).Distinct()];
                edited.HdrSupported |= discovered.HdrSupported;
                edited.MaximumDpiPercent = Math.Max(edited.MaximumDpiPercent, discovered.MaximumDpiPercent);
            }
        }

        if ((request.PluginEdits.Count > 0 || request.DeviceProfiles is not null)
            && request.PluginDevice.Length > 0
            && request.PluginId.Length > 0)
        {
            var scope = FindOrAddSaveScope(config, request.PluginDevice, request.PluginId);
            foreach (var (settingId, value) in request.PluginEdits)
            {
                PluginSettingsResolver.Store(scope, settingId, value);
            }

            if (request.DeviceProfiles is not null)
            {
                // A deleted profile is also removed from every layer that selected it, so that layer
                // falls back to the one below instead of naming nothing.
                foreach (var removed in scope.Profiles.Select(profile => profile.ProfileId)
                             .Except(request.DeviceProfiles.Select(profile => profile.ProfileId),
                                 StringComparer.Ordinal).ToArray())
                {
                    ProfileEdits.RemoveFanCurveReferences(config.Profiles, removed);
                }

                scope.Profiles = [.. request.DeviceProfiles];
            }
        }

        config.Splash = preparedSplash;
        return (config, new SaveChanges(
            config.SteamInputManagementEnabled != shimWas,
            !autostartWas && config.SteamAutostartTakeoverAccepted,
            !managersWas && config.OtherManagersTakeoverAccepted));
    }

    private static PluginSettingsScope FindOrAddSaveScope(
        AppConfig config,
        string deviceDefinitionId,
        string pluginId)
    {
        var scope = config.DeviceIntegration.PluginSettings.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, deviceDefinitionId, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, pluginId, StringComparison.Ordinal));
        if (scope is not null)
        {
            return scope;
        }

        scope = new PluginSettingsScope
        {
            DeviceDefinitionId = deviceDefinitionId,
            PluginId = pluginId
        };
        config.DeviceIntegration.PluginSettings.Add(scope);
        return scope;
    }
}
