using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SteamUiToolkit;

namespace WSGM.Core;

/// <summary>
///     Game Tools page policy: a tool's status, valid actions, warnings and setting values, and the labels both
///     interfaces show, so the Overlay and Steam present the same decisions.
/// </summary>
/// <remarks>Everything here is pure: callers read the folder, the manifest and the configuration file first.</remarks>
internal static class GameToolStatus
{
    /// <summary>Decides a tool's status and valid actions.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="availability">The game's availability: <c>ready</c>, <c>running</c> or a reason it has no folder.</param>
    /// <param name="reason">Why the game is not ready, or null.</param>
    /// <param name="runtimeArchitecture">The chosen runtime's architecture, or null when the user must choose.</param>
    /// <param name="manifest">The installed manifest, or null.</param>
    /// <param name="inspection">The target directory's inspection, or null when it couldn't be inspected.</param>
    /// <param name="latest">The latest known release of <paramref name="channel" />, or null.</param>
    /// <param name="channel">The channel the installation follows, or the default one before installation.</param>
    /// <param name="upscalers">Standalone upscaler libraries the game ships.</param>
    /// <returns>The evaluation.</returns>
    /// <remarks>
    ///     An installed tool with more than one channel also offers <c>switchChannel</c>; moving to another channel
    ///     is always the user's explicit choice, never an update.
    /// </remarks>
    internal static GameToolEvaluation Evaluate(GameToolDefinition tool, string availability, string? reason,
        string? runtimeArchitecture, GameToolManifest? manifest, GameToolInspection? inspection, HostedRelease? latest,
        GameToolChannel channel, IReadOnlyList<GameUpscalerFile> upscalers)
    {
        var folderReady = HasFolder(availability);
        var incompatible = runtimeArchitecture is null
            ? "Choose the game's program first."
            : GameToolInstaller.Incompatible(tool, runtimeArchitecture);
        var relation = Relation(tool, manifest, latest);
        var changesConfig = relation == GameToolVersionRelation.UpdateAvailable
                            && ChangesConfig(tool, manifest, latest);
        List<GameToolIssue> conflicts = [];
        List<string> warnings = [];
        List<string> actions = [];
        string status;
        string statusText;
        if (!folderReady)
        {
            status = "unavailable";
            statusText = reason ?? "Not available.";
        }
        else if (manifest is not null)
        {
            if (manifest.State != GameToolManifestState.Installed)
            {
                status = "modified";
                statusText = "An earlier change was interrupted. Remove to finish undoing it.";
                actions.Add("remove");
            }
            else if (inspection is null)
            {
                status = "modified";
                statusText = "The folder " + tool.Name + " was installed into isn't available. If it is on a drive "
                             + "that isn't connected, connect it first.";
                actions.Add("remove");
            }
            else if (inspection.Missing.Count > 0 || inspection.Modified.Count > 0)
            {
                status = "modified";
                statusText = "Some of " + tool.Name + "'s files are missing or were changed.";
                actions.AddRange(["repair", "remove"]);
            }
            else
            {
                status = relation == GameToolVersionRelation.UpdateAvailable ? "updateAvailable" : "installed";
                statusText = relation switch
                {
                    GameToolVersionRelation.UpdateAvailable => "Version "
                                                               + channel.Display(latest!.Version, latest.Asset.Name)
                                                               + " is available.",
                    GameToolVersionRelation.NewerThanLatest => "Newer than the latest "
                                                               + channel.Label.ToLowerInvariant() + " release.",
                    _ => manifest.Enabled ? "Installed as " + manifest.ProxyName + "." : "Disabled for this game. Files and settings are kept."
                };
                if (relation == GameToolVersionRelation.UpdateAvailable)
                {
                    actions.Add("update");
                }

                if (tool.Channels.Length > 1)
                {
                    actions.Add("switchChannel");
                }

                actions.AddRange(["configure", "repair", "remove"]);
                if (!manifest.Enabled)
                {
                    actions.RemoveAll(action => action is "update" or "repair" or "switchChannel");
                }
            }

            foreach (var copy in inspection?.Unmanaged ?? [])
            {
                conflicts.Add(new GameToolIssue(copy.RelativePath, "needsReview",
                    "Another copy of " + tool.Name + " (" + copy.Version + ") is in the folder."));
            }
        }
        else if (inspection is { Unmanaged.Count: > 0 })
        {
            status = "conflict";
            var versions = inspection.Unmanaged.Select(copy => copy.Version).Distinct(StringComparer.Ordinal).Count();
            statusText = tool.Name + " was installed outside WSGM.";
            foreach (var copy in inspection.Unmanaged)
            {
                conflicts.Add(new GameToolIssue(copy.RelativePath, versions > 1 ? "needsReview" : "unmanagedTool",
                    versions > 1
                        ? "Several copies of " + tool.Name + " with different versions are installed."
                        : tool.Name + " " + copy.Version + " installed outside WSGM."));
            }

            if (incompatible is null)
            {
                actions.Add("replaceExisting");
            }
        }
        else
        {
            status = "notInstalled";
            statusText = incompatible ?? "Not installed.";
            if (incompatible is null)
            {
                actions.Add("install");
            }
        }

        if (manifest is null && folderReady && UpscalerWarning(tool, upscalers) is { } missing)
        {
            warnings.Add(missing);
        }

        if (changesConfig)
        {
            warnings.Add("This update changes " + tool.PrimaryConfig + "'s format. Updating replaces it with the "
                         + "new default file and applies WSGM's settings again.");
        }

        if (availability == "running")
        {
            actions.Clear();
        }

        var canEdit = actions.Contains("install") || actions.Contains("configure");
        return new GameToolEvaluation(status, statusText, incompatible, actions, conflicts, warnings, changesConfig,
            canEdit || (manifest is not null && folderReady), canEdit);
    }

    /// <summary>How an installation relates to a channel's latest release.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="manifest">The installed manifest, or null.</param>
    /// <param name="latest">The channel's latest release, or null when unknown.</param>
    /// <returns>Unknown without an installation or a known release.</returns>
    internal static GameToolVersionRelation Relation(GameToolDefinition tool, GameToolManifest? manifest,
        HostedRelease? latest)
    {
        return manifest is null
            ? GameToolVersionRelation.Unknown
            : GameToolVersions.Relate(tool,
                manifest.ReleaseVersion.Length > 0 ? manifest.ReleaseVersion : manifest.Version, manifest.ReleaseId,
                latest?.Version, latest?.Id);
    }

    /// <summary>Whether installing a release replaces the installation's configuration format.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="manifest">The installed manifest, or null.</param>
    /// <param name="latest">The release that would be installed, or null.</param>
    /// <returns>True when both families are known and differ; the user must then confirm <c>replaceConfig</c>.</returns>
    internal static bool ChangesConfig(GameToolDefinition tool, GameToolManifest? manifest, HostedRelease? latest)
    {
        var family = latest is null ? "" : GameToolVersions.Family(tool, latest.Version);
        return manifest is { Family.Length: > 0 } && family.Length > 0 && manifest.Family != family;
    }

    /// <summary>Whether the game has a folder to work in, even while it runs.</summary>
    /// <param name="availability">The availability value.</param>
    /// <returns>True for <c>ready</c> and <c>running</c>.</returns>
    internal static bool HasFolder(string availability)
    {
        return availability is "ready" or "running";
    }

    /// <summary>The warning shown when a game ships none of the upscalers a tool needs, or null.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="upscalers">The libraries found.</param>
    /// <returns>The warning naming the catalog's required families, or null when one is present or none is required.</returns>
    internal static string? UpscalerWarning(GameToolDefinition tool, IReadOnlyList<GameUpscalerFile> upscalers)
    {
        if (tool.RequiresUpscaler.Length == 0 || upscalers.Any(file => tool.RequiresUpscaler.Any(requirement =>
                requirement.Family.Equals(file.Family, StringComparison.Ordinal))))
        {
            return null;
        }

        var labels = tool.RequiresUpscaler.Select(requirement => requirement.Label).ToArray();
        var named = labels.Length == 1
            ? labels[0]
            : string.Join(", ", labels[..^1]) + " or " + labels[^1];
        return "No standalone " + named + " library was found. The game may still support one built in; "
               + tool.Name + " only works when the game offers an upscaler.";
    }

    /// <summary>The single anti-cheat warning a game's page shows, or null when none was found.</summary>
    /// <param name="tools">The catalog tools, whose own warnings are combined.</param>
    /// <param name="antiCheat">The detected anti-cheat systems.</param>
    /// <returns>The finished text naming what was found.</returns>
    internal static string? AntiCheatWarning(IEnumerable<GameToolDefinition> tools, IReadOnlyList<string> antiCheat)
    {
        if (antiCheat.Count == 0)
        {
            return null;
        }

        var warnings = tools.Select(tool => tool.AntiCheatWarning).Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal);
        return string.Join(" ", ["Anti-cheat found: " + string.Join(", ", antiCheat) + ".", .. warnings]);
    }

    /// <summary>A setting's value and note for one game.</summary>
    /// <param name="setting">The setting.</param>
    /// <param name="manifest">The installed manifest, or null.</param>
    /// <param name="iniValues">Raw values read from the installed configuration file, or null when it is missing.</param>
    /// <param name="vendors">Lowercase GPU vendors present.</param>
    /// <returns>The value.</returns>
    internal static GameToolSettingValue SettingValue(GameToolSetting setting, GameToolManifest? manifest,
        IReadOnlyDictionary<string, string>? iniValues, IReadOnlyList<string> vendors)
    {
        var notes = vendors.Select(vendor => setting.VendorNotes.GetValueOrDefault(vendor))
            .Where(note => note is not null).ToArray();
        var note = notes.Length > 0 ? string.Join(" ", notes) : null;
        if (manifest is null)
        {
            return new GameToolSettingValue(setting.Default, null, true, note);
        }

        if (iniValues is null || !iniValues.TryGetValue(setting.Id, out var raw))
        {
            return new GameToolSettingValue(manifest.Settings.GetValueOrDefault(setting.Id) ?? setting.Default, null,
                false, "Not available in the installed version.");
        }

        var matched = setting.Options.FirstOrDefault(option =>
            option.Ini.Equals(raw, StringComparison.OrdinalIgnoreCase))?.Value;
        return new GameToolSettingValue(matched ?? "", raw, true,
            matched is null ? "Set to \"" + raw + "\" in the configuration file." : note);
    }

    /// <summary>Decides what a game's page shows for one add-on.</summary>
    /// <param name="addon">The add-on.</param>
    /// <param name="manifest">The tool's installed manifest, or null.</param>
    /// <param name="program">The file name of the program the add-on would run in: the installed one, else the chosen one.</param>
    /// <param name="support">The cached support list, or null before it was read.</param>
    /// <param name="latest">The add-on's latest known build, or null.</param>
    /// <param name="vendors">Lowercase GPU vendors present.</param>
    /// <param name="showOptions">Whether the tool's options are shown.</param>
    /// <param name="canEditOptions">Whether the tool's options can be changed now.</param>
    /// <returns>The evaluation.</returns>
    /// <remarks>
    ///     A game the list doesn't name gets no row, so nothing is offered that the add-on would ignore. A failed read
    ///     shows the row with the reason, locked; an installed add-on is always shown so it can be removed.
    /// </remarks>
    internal static GameToolAddonEvaluation Addon(GameToolAddon addon, GameToolManifest? manifest, string? program,
        GameToolAddonSupport? support, HostedRelease? latest, IReadOnlyList<string> vendors, bool showOptions,
        bool canEditOptions)
    {
        var record = manifest?.Addons.FirstOrDefault(item => item.Id == addon.Id);
        var installed = record is not null;
        var supportState = support?.Executables is null ? "unknown"
            : program is not null && support.Supports(program) ? "supported"
            : "unsupported";
        var notes = vendors.Select(vendor => addon.VendorNotes.GetValueOrDefault(vendor))
            .Where(note => note is not null).ToArray();
        var update = installed && latest is not null && latest.Id != record!.ReleaseId;
        string text;
        if (installed)
        {
            var latestDate = latest is null ? null : GameToolVersions.BuildDate(latest.Asset);
            text = update
                ? latestDate is null
                    ? "A newer build is available."
                    : "The build of " + GameToolVersions.DisplayDate(latestDate) + " is available."
                : record!.BuildDate.Length > 0
                    ? "Installed, the build of " + GameToolVersions.DisplayDate(record.BuildDate) + "."
                    : "Installed.";
        }
        else
        {
            text = supportState switch
            {
                "supported" => addon.Name + " supports " + program + ".",
                "unsupported" => addon.Name + " doesn't support " + (program ?? "this game") + ".",
                _ => support?.Error ?? "Checking which games " + addon.Name + " supports…"
            };
        }

        var show = showOptions && (installed || supportState == "supported" || support?.Error is not null);
        return new GameToolAddonEvaluation(show, supportState, installed,
            supportState == "supported" && vendors.Any(vendor => addon.Vendors.Contains(vendor, StringComparer.Ordinal)),
            canEditOptions && (installed || supportState == "supported"), text,
            notes.Length > 0 ? string.Join(" ", notes) : null, update);
    }

    /// <summary>The wire id of a confidence.</summary>
    /// <param name="confidence">The confidence.</param>
    /// <returns><c>confirmed</c>, <c>structural</c>, <c>single</c> or <c>needsChoice</c>.</returns>
    internal static string ConfidenceId(GameRuntimeConfidence confidence)
    {
        return confidence switch
        {
            GameRuntimeConfidence.Confirmed => "confirmed",
            GameRuntimeConfidence.Structural => "structural",
            GameRuntimeConfidence.Single => "single",
            _ => "needsChoice"
        };
    }

    /// <summary>Explains how the runtime was chosen.</summary>
    /// <param name="confidence">The confidence.</param>
    /// <returns>The sentence shown beside the program, empty when the user must choose.</returns>
    internal static string ConfidenceLabel(GameRuntimeConfidence confidence)
    {
        return confidence switch
        {
            GameRuntimeConfidence.Confirmed => "You chose this program",
            GameRuntimeConfidence.Structural => "Identified from the folder layout or Steam's launch target",
            GameRuntimeConfidence.Single => "The only likely program in the folder",
            _ => ""
        };
    }

    /// <summary>The wire id of a role.</summary>
    /// <param name="role">The role.</param>
    /// <returns><c>game</c>, <c>launcher</c>, <c>helper</c> or <c>unknown</c>.</returns>
    internal static string RoleId(GameRuntimeRole role)
    {
        return role switch
        {
            GameRuntimeRole.Game => "game",
            GameRuntimeRole.Launcher => "launcher",
            GameRuntimeRole.Helper => "helper",
            _ => "unknown"
        };
    }

    /// <summary>The role shown to the user.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The label.</returns>
    internal static string RoleLabel(GameRuntimeRole role)
    {
        return role switch
        {
            GameRuntimeRole.Game => "Likely the game",
            GameRuntimeRole.Launcher => "Launcher or settings",
            GameRuntimeRole.Helper => "Installer or helper",
            _ => "Program"
        };
    }

    /// <summary>A conflict kind shown to the user.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The label.</returns>
    internal static string KindLabel(string kind)
    {
        return kind switch
        {
            "unmanagedTool" => "Installed outside WSGM",
            "needsReview" => "Needs review",
            _ => "Game file in the way"
        };
    }

    /// <summary>Finds a game's install root, and for a shortcut the program it starts.</summary>
    /// <param name="shortcut">Whether the id is a non-Steam shortcut.</param>
    /// <param name="details">Steam's answer for the game.</param>
    /// <returns>The root (empty when there is none), the shortcut target or null, and why there is no root.</returns>
    /// <remarks>
    ///     A shortcut's start folder is the game root only when it contains the program and is not too broad;
    ///     otherwise the program's own folder is, under the same guard as a browsed program.
    /// </remarks>
    internal static (string Root, string? DirectTarget, string Reason) InstallRoot(bool shortcut,
        SteamAppDetails details)
    {
        if (!shortcut)
        {
            var folder = Clean(details.InstallFolder);
            return folder.Length > 0 && Path.IsPathFullyQualified(folder)
                ? (Path.GetFullPath(folder), null, "")
                : ("", null, "This game isn't installed.");
        }

        var target = Clean(LaunchWrapperCommand.OriginalsFrom(true, details).Target);
        if (target.Length == 0 || !Path.IsPathFullyQualified(target)
                               || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("", null, "This shortcut doesn't start a Windows program WSGM can modify.");
        }

        target = Path.GetFullPath(target);
        var start = Clean(details.ShortcutStartDir);
        var targetFolder = Path.GetDirectoryName(target) ?? "";
        if (start.Length > 0 && Path.IsPathFullyQualified(start) && StoragePaths.IsUnder(start, target)
            && GameFolderGuard.Reject(Path.Combine(start, "x.exe")) is null)
        {
            return (Path.GetFullPath(start), target, "");
        }

        // Windows, the profile, Downloads and other shared folders are never a game folder.
        return GameFolderGuard.Reject(target) is null
            ? (targetFolder, target, "")
            : ("", null, "This shortcut starts a program in " + targetFolder + ", a folder other programs "
                         + "share. WSGM only adds files to a game's own folder.");
    }

    private static string Clean(string? path)
    {
        return (path ?? "").Trim().Trim('"').Trim();
    }
}
