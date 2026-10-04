using System;
using System.Collections.Generic;
using System.Linq;

using static WSGM.Core.AppConfigDefaults;

namespace WSGM.Core;

internal sealed record ConfigRuleResult<T>(T Value, IReadOnlyList<string> Diagnostics);

internal static class AppConfigRules
{
    /// <summary>
    ///     An explicit JSON null ("StartupApps": null) deserializes over the
    ///     property initializer; replace nulls with fresh defaults so a hand-edited
    ///     config can never NRE the shell later (which would kill it before the panic
    ///     handler runs). New nested object/list members belong in this list too.
    /// </summary>
    internal static ConfigRuleResult<AppConfig> Normalize(AppConfig config)
    {
        List<string> diagnostics = [];
        ConfigRepair.NormalizeEnums(config);
        config.StartupApps ??= [];
        config.PluginInstances = [.. (config.PluginInstances ?? []).Where(static instance => instance is not null)];
        config.DeviceIntegration ??= new DeviceIntegrationConfig();
        diagnostics.AddRange(DeviceConfigurationRules.Normalize(config.DeviceIntegration));
        config.Performance ??= new PerformanceConfig();
        config.Profiles ??= new ProfileConfig();
        diagnostics.AddRange(ProfileConfigRules.Normalize(config.Profiles, config.DeviceIntegration));
        config.Artwork ??= new ArtworkConfig();
        diagnostics.AddRange(ArtworkRules.Normalize(config.Artwork));
        config.GameLibrary ??= new GameLibraryConfig();
        diagnostics.AddRange(GameLibraryRules.Normalize(config.GameLibrary));
        config.Themes ??= new ThemesConfig();
        diagnostics.AddRange(ThemeRules.Normalize(config.Themes));
        config.Animations ??= new AnimationsConfig();
        diagnostics.AddRange(AnimationRules.Normalize(config.Animations));
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
        diagnostics.AddRange(GameModeLaunchRules.Normalize(config.GameModeLaunch));
        config.GameModeLaunchRecovery ??= new GameModeLaunchRecovery();
        config.GameModeLaunchRecovery.PendingReturnAudio =
            GameModeLaunchRules.NormalizeAudioProfile(config.GameModeLaunchRecovery.PendingReturnAudio);
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
            tab.Name ??= "";
            tab.FilterTree ??= new FilterNode { Kind = FilterKind.Merge };
            diagnostics.AddRange(LibraryFilterRules.Normalize(tab.FilterTree));
        }

        AssignMissingTabIds(config.CustomTabs);

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
        diagnostics.AddRange(SplashRules.Normalize(config.Splash).Diagnostics);
        return new ConfigRuleResult<AppConfig>(config, diagnostics);
    }

    /// <summary>
    ///     Gives a stored tab without an id the same id on every load: <c>tab-&lt;index&gt;-&lt;name&gt;</c>, the
    ///     name lowered with everything outside a-z and 0-9 replaced by '-', and -2, -3 and so on appended when
    ///     another tab already holds it. Steam-side state keyed by the id then survives until the next save.
    /// </summary>
    private static void AssignMissingTabIds(List<CustomTabConfig> tabs)
    {
        HashSet<string> taken = new(StringComparer.Ordinal);
        foreach (var tab in tabs)
        {
            if (!string.IsNullOrWhiteSpace(tab.Id))
            {
                taken.Add(tab.Id);
            }
        }

        for (var index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index];
            if (!string.IsNullOrWhiteSpace(tab.Id))
            {
                continue;
            }

            var slug = tab.Name.ToLowerInvariant().ToCharArray();
            for (var position = 0; position < slug.Length; position++)
            {
                if (slug[position] is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
                {
                    slug[position] = '-';
                }
            }

            var baseId = $"tab-{index}-{new string(slug)}";
            var id = baseId;
            for (var ordinal = 2; !taken.Add(id); ordinal++)
            {
                id = $"{baseId}-{ordinal}";
            }

            tab.Id = id;
        }
    }
}
