using System.Text.Json.Nodes;

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
        json.Contains("\"GameModeBootEnabled\"", System.StringComparison.Ordinal);

    /// <summary>Applies every migration rule to one parsed configuration document.</summary>
    /// <param name="root">The document root, modified in place.</param>
    /// <returns>True when a rule changed something.</returns>
    internal static bool Apply(JsonObject root)
    {
        return MigrateSignInStart(root);
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
}
