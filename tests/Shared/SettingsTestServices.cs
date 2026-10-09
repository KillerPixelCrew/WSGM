// SPDX-License-Identifier: MIT

using LibHandheld.Contracts;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.Testing;

/// <summary>
///     Settings services that touch nothing on this machine: no displays, Core Audio, task scheduler, Steam,
///     update state or config.json. Linked into several test projects, so it uses no test-framework API.
/// </summary>
internal static class SettingsTestServices
{
    /// <summary>Builds inert services over an in-memory saved configuration.</summary>
    /// <param name="saved">What config.json holds; a save merges onto a copy, and a re-read returns it.</param>
    /// <param name="calls">Records every external action and report by name, when supplied.</param>
    /// <returns>Services a test overrides member by member with a <c>with</c> expression.</returns>
    internal static SettingsViewModel.SettingsServices Inert(AppConfig saved, List<string>? calls = null)
    {
        var current = ConfigJson.Clone(saved, ConfigJsonContext.Tolerant.AppConfig);
        return new SettingsViewModel.SettingsServices(
            () => new DisplayArrangement([], "inert", DateTimeOffset.UnixEpoch),
            _ => null,
            () => [],
            () => [],
            () => { },
            () => { },
            request =>
            {
                var (merged, changes) = SettingsSaveMerge.Apply(
                    ConfigJson.Clone(current, ConfigJsonContext.Tolerant.AppConfig), request, request.Splash);
                current = merged;
                return Task.FromResult(new SettingsViewModel.SaveResult(merged, [], null, changes));
            },
            _ => calls?.Add("reconcile"),
            () => "",
            (message, _) => calls?.Add(message),
            () => new ModernStandbyReport(true, "This machine has not been in standby since it booted.", []),
            () =>
            {
                calls?.Add("scan-autostart");
                return [];
            },
            _ =>
            {
                calls?.Add("apply-autostart");
                return new SteamAutostartTakeoverResult([], [], []);
            },
            _ => Task.FromResult(new UpdateState()),
            (_, _, _) => Task.FromResult(""),
            _ => calls?.Add("run-setup"),
            () => null,
            () => new PluginPackagePage(PluginPackageCatalog.Empty, [], null, []),
            (_, _) => Task.FromResult(""),
            () => false,
            () => calls?.Add("repair"),
            _ => AudioDiscovery.Empty,
            () => new UpdateState(),
            () =>
            {
                calls?.Add("detect-managers");
                return [];
            },
            _ =>
            {
                calls?.Add("apply-managers");
                return new OtherManagersResult([], [], []);
            },
            () =>
            {
                calls?.Add("load-persisted");
                return ConfigJson.Clone(current, ConfigJsonContext.Tolerant.AppConfig);
            });
    }

    /// <summary>A view model over <paramref name="config" /> with inert services, showing every plugin's settings.</summary>
    /// <param name="config">The configuration the model edits, which is also what config.json holds.</param>
    /// <returns>The view model.</returns>
    internal static SettingsViewModel Model(AppConfig config)
    {
        return new SettingsViewModel(config, Inert(config));
    }

    /// <summary>A view model over <paramref name="config" /> with inert services, selecting the installed plugin.</summary>
    /// <param name="config">The configuration the model edits, which is also what config.json holds.</param>
    /// <param name="definition">The current exact model, or null when the machine is unsupported.</param>
    /// <returns>The view model.</returns>
    internal static SettingsViewModel Model(AppConfig config, HandheldDefinition? definition)
    {
        return new SettingsViewModel(config, Inert(config), definition);
    }
}
