using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>Rewrites a stored configuration document from a retired shape into the current one.
///
/// This runs on the raw JSON before typed deserialization, so <see cref="ConfigStore.Load"/> and
/// <see cref="ConfigStore.LoadForMutation"/> see the same values and neither writes a half-migrated
/// file. Every rule is pure and idempotent, and a document that carries both the retired and the
/// current key keeps the current one: a newer build's save must never be undone by an older key
/// left behind in the same file.</summary>
internal static class ConfigMigrations
{
    /// <summary>Whether <paramref name="json"/> may contain a retired key worth a JSON pass.
    /// Cheap enough to run on every load, so the pass itself stays off the common path.</summary>
    /// <param name="json">The raw file contents.</param>
    /// <returns>True when the document mentions a key this class rewrites.</returns>
    internal static bool MayNeedMigration(string json) =>
        json.Contains("\"GameModeBootEnabled\"", StringComparison.Ordinal)
        || json.Contains("\"DisplayManagement\"", StringComparison.Ordinal)
        || json.Contains("\"DisplayProfiles\"", StringComparison.Ordinal)
        || json.Contains("\"DisplayRoutes\"", StringComparison.Ordinal);

    /// <summary>Applies every migration rule to one parsed configuration document.</summary>
    /// <param name="root">The document root, modified in place.</param>
    /// <returns>True when a rule changed something.</returns>
    internal static bool Apply(JsonObject root)
    {
        // The sign-in rule reads DisplayRoutes, so it has to run before the launch rule removes it.
        bool changed = MigrateSignInStart(root);
        return MigrateGameModeLaunch(root) || changed;
    }

    /// <summary>Splits the retired single game-mode boot switch into the sign-in start choice and
    /// the start mode. Desktop residency used to be a side effect of enabling route automation, so
    /// that combination is what a Desktop start migrates from.</summary>
    private static bool MigrateSignInStart(JsonObject root)
    {
        if (root["GameModeBootEnabled"] is not { } legacy)
        {
            return false;
        }
        root.Remove("GameModeBootEnabled");
        if (root.ContainsKey(nameof(AppConfig.StartAtSignIn)) || root.ContainsKey(nameof(AppConfig.StartMode)))
        {
            // Written by a build that already knows both keys; the retired one is stale.
            return true;
        }

        bool gameModeBoot = legacy is JsonValue value && value.TryGetValue(out bool enabled) && enabled;
        bool desktopResident = root["DisplayRoutes"] is JsonObject routes
            && routes["Enabled"] is JsonValue routesEnabled
            && routesEnabled.TryGetValue(out bool routesOn)
            && routesOn;

        root[nameof(AppConfig.StartAtSignIn)] = gameModeBoot || desktopResident;
        // Only a configuration that actually started a resident desktop carries a Desktop
        // preference. With neither flag set the mode was never expressed, so it keeps the default.
        root[nameof(AppConfig.StartMode)] =
            (!gameModeBoot && desktopResident ? SessionStartMode.Desktop : SessionStartMode.Game)
            .ToString();
        return true;
    }

    /// <summary>Folds the four retired display-management modes, the fixed per-monitor profiles and
    /// the route bindings into one launch configuration.
    ///
    /// The four modes collapse to two because three of them differed only in what they captured
    /// behind the user's back. Off becomes Default rather than "nothing at all": Game Mode has
    /// always needed the scaling handling that Default performs, and the old Off left a handheld
    /// running the desktop's scaling in Big Picture.</summary>
    private static bool MigrateGameModeLaunch(JsonObject root)
    {
        bool present = root.Remove("DisplayManagement", out JsonNode? mode)
            | root.Remove("DisplayProfiles", out JsonNode? profiles)
            | root.Remove("DisplayRoutes", out JsonNode? routes);
        if (!present) { return false; }
        if (root.ContainsKey(nameof(AppConfig.GameModeLaunch)))
        {
            // Written by a build that already knows the launch shape; the retired keys are stale.
            return true;
        }

        GameModeLaunchConfiguration launch = new();
        // Fixed profiles first, so an enabled route binding's own layout replaces them rather than
        // merging with them. The two were independent features and could disagree.
        if (mode?.GetValue<string>() == nameof(LegacyDisplayManagement.FixedProfiles))
        {
            (launch.GameLayout, launch.DesktopLayout) = FromFixedProfiles(profiles as JsonArray);
        }
        if (Read(routes, ConfigJsonContext.Default.LegacyDisplayRoutes) is { Enabled: true } enabled)
        {
            ApplyRoutes(launch, enabled);
        }

        launch.Kind = launch.GameLayout is null ? GameModeLaunchKind.Default : GameModeLaunchKind.Custom;
        launch.Return = launch.DesktopLayout is null
            ? GameModeReturn.EntryArrangement
            : GameModeReturn.DesktopLayout;
        launch.KnownDisplays = [.. Catalog(launch)];
        root[nameof(AppConfig.GameModeLaunch)] =
            JsonSerializer.SerializeToNode(launch, ConfigJsonContext.Default.GameModeLaunchConfiguration);
        return true;
    }

    /// <summary>Builds the Game and Desktop layouts from the retired per-monitor profiles.
    ///
    /// The old profiles recorded a GDI source name and a registry device key, neither of which is a
    /// display identity Windows can resolve back to a monitor, so the outputs carry an empty
    /// identity that matches nothing. Settings shows those as needing confirmation and entry
    /// refuses them, which is the honest outcome: the values are worth keeping, the binding is
    /// genuinely unknown. Displays are laid out left to right in their stored order, because the
    /// old shape never recorded where any of them sat.</summary>
    private static (DisplayLayout? Game, DisplayLayout? Desktop) FromFixedProfiles(JsonArray? profiles)
    {
        if (profiles is null) { return (null, null); }
        List<DisplayLayoutOutput> game = [], desktop = [];
        int gameX = 0, desktopX = 0;
        foreach (JsonNode? node in profiles)
        {
            if (Read(node, ConfigJsonContext.Default.LegacyMonitorDisplayProfile) is not { } profile) { continue; }
            DisplayTargetIdentity identity = Unresolved(profile.DisplayName);
            if (Output(identity, profile.Game, profile.HdrAvailable, gameX) is { } gameOutput)
            {
                game.Add(gameOutput);
                gameX += gameOutput.Width;
            }
            if (Output(identity, profile.Desktop, profile.HdrAvailable, desktopX) is { } desktopOutput)
            {
                desktop.Add(desktopOutput);
                desktopX += desktopOutput.Width;
            }
        }
        return (Valid(game), Valid(desktop));
    }

    private static DisplayLayoutOutput? Output(
        DisplayTargetIdentity identity, LegacyDisplayModeValues values, bool hdrAvailable, int x) =>
        values is { Width: > 0, Height: > 0 }
            ? new(identity, x, 0, values.Width, values.Height, DisplayRefresh.FromHertz(values.RefreshRate),
                DpiPercent: values.DpiPercent, Hdr: hdrAvailable ? values.HdrEnabled : null)
            : null;

    private static void ApplyRoutes(GameModeLaunchConfiguration launch, LegacyDisplayRoutes routes)
    {
        launch.EnterActions = Steps(routes.EnterGameMode);
        launch.LeaveActions = Steps(routes.LeaveGameMode);
        launch.DesktopStartupActions = Steps(routes.DesktopStartup);
        launch.DesktopWakeActions = Steps(routes.DesktopWake);
        launch.WaitForDisplay = routes.EnterGameMode?.Target;
        launch.GameLayout = Layout(routes.EnterGameMode?.Profile) ?? launch.GameLayout;
        launch.DesktopLayout = Layout(routes.LeaveGameMode?.Profile) ?? launch.DesktopLayout;
    }

    /// <summary>Reads a stored native profile back as an editable layout. An undecodable blob is
    /// dropped; it was replay data for a topology that may no longer exist, and no part of the file
    /// is worth losing over it.</summary>
    private static DisplayLayout? Layout(DisplayProfile? profile) =>
        profile is null ? null : DisplayLayouts.FromProfile(profile);

    private static List<PluginActionStep> Steps(LegacyDisplayRouteBinding? binding) =>
        binding?.Plugin is { } plugin && !string.IsNullOrWhiteSpace(binding.ActionId)
            ? [new()
            {
                Plugin = plugin,
                ActionId = binding.ActionId,
                Arguments = new(binding.Arguments ?? []),
                TimeoutSeconds = Math.Clamp(binding.TimeoutSeconds, 1, 120),
            }]
            : [];

    /// <summary>Seeds the display catalog so the editor can show what the migrated layouts name,
    /// including a display that is not plugged in right now.</summary>
    private static IEnumerable<KnownDisplay> Catalog(GameModeLaunchConfiguration launch)
    {
        List<DisplayTargetIdentity> seen = [];
        foreach (DisplayTargetIdentity target in new[] { launch.WaitForDisplay }
            .Concat(launch.GameLayout?.Outputs.Select(output => output.Target) ?? [])
            .Concat(launch.DesktopLayout?.Outputs.Select(output => output.Target) ?? [])
            .OfType<DisplayTargetIdentity>())
        {
            if (target.DevicePath.Length == 0 || seen.Exists(other => other.Matches(target))) { continue; }
            seen.Add(target);
            yield return new() { Target = target };
        }
    }

    /// <summary>An identity that deliberately matches no monitor, for a value whose display is
    /// known to the user but not to Windows.</summary>
    private static DisplayTargetIdentity Unresolved(string name) => new("", null, null, name, 0, 0, 0);

    private static DisplayLayout? Valid(List<DisplayLayoutOutput> outputs)
    {
        DisplayLayout layout = new(outputs);
        return outputs.Count > 0 && DisplayLayouts.Describe(layout) is null ? layout : null;
    }

    private static T? Read<T>(JsonNode? node, JsonTypeInfo<T> type) where T : class
    {
        try { return node is null ? null : JsonSerializer.Deserialize(node, type); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>The retired display-management values, as the strings a stored file holds.</summary>
    private enum LegacyDisplayManagement
    {
        Off,
        DpiOnly,
        AutomaticProfiles,
        FixedProfiles,
    }
}
