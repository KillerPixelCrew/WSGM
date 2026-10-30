using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SteamUiToolkit;
using WSGM.Core.Downloads;

namespace WSGM.Core.GameTools;

/// <summary>A file that blocks installation or needs review.</summary>
/// <param name="RelativePath">The file relative to the target directory.</param>
/// <param name="Kind"><c>unmanagedTool</c> or <c>needsReview</c>.</param>
/// <param name="Message">The user-facing explanation.</param>
internal sealed record GameToolIssue(string RelativePath, string Kind, string Message);

/// <summary>What one tool's page shows for one game, decided from facts already read from disk.</summary>
/// <param name="Status"><c>notInstalled</c>, <c>installed</c>, <c>updateAvailable</c>, <c>modified</c>, <c>conflict</c> or <c>unavailable</c>.</param>
/// <param name="StatusText">A short user-facing status line.</param>
/// <param name="IncompatibleReason">Why the tool can't be installed for the chosen runtime, or null.</param>
/// <param name="Actions">Commands currently valid for this tool.</param>
/// <param name="Conflicts">Files that block installation or need review.</param>
/// <param name="Warnings">Things the user should know before acting.</param>
/// <param name="UpdateChangesConfig">Whether the available update changes the configuration format.</param>
/// <param name="ShowOptions">Whether the file name and settings are shown at all.</param>
/// <param name="CanEditOptions">Whether the file name and settings can be changed now.</param>
internal sealed record GameToolEvaluation(
    string Status,
    string StatusText,
    string? IncompatibleReason,
    IReadOnlyList<string> Actions,
    IReadOnlyList<GameToolIssue> Conflicts,
    IReadOnlyList<string> Warnings,
    bool UpdateChangesConfig,
    bool ShowOptions,
    bool CanEditOptions);

/// <summary>A setting's displayed value for one game.</summary>
/// <param name="Value">The selected option value; empty when the file holds a value no option matches.</param>
/// <param name="IniValue">The raw value in the configuration file, or null when not installed or missing.</param>
/// <param name="Available">False when the installed version's configuration file has no such key.</param>
/// <param name="Note">A vendor-specific or availability note, or null.</param>
internal sealed record GameToolSettingValue(string Value, string? IniValue, bool Available, string? Note);

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
    /// <param name="latest">The latest known release, or null.</param>
    /// <param name="upscalers">Standalone upscaler libraries the game ships.</param>
    /// <returns>The evaluation.</returns>
    internal static GameToolEvaluation Evaluate(GameToolDefinition tool, string availability, string? reason,
        string? runtimeArchitecture, GameToolManifest? manifest, GameToolInspection? inspection, HostedRelease? latest,
        IReadOnlyList<GameUpscalerFile> upscalers)
    {
        var folderReady = HasFolder(availability);
        var incompatible = runtimeArchitecture is null
            ? "Choose the game's program first."
            : GameToolInstaller.Incompatible(tool, runtimeArchitecture);
        var relation = manifest is null
            ? GameToolVersionRelation.Unknown
            : GameToolVersions.Relate(tool,
                manifest.ReleaseVersion.Length > 0 ? manifest.ReleaseVersion : manifest.Version, manifest.ReleaseId,
                latest?.Version, latest?.Id);
        var latestFamily = latest is null ? "" : GameToolVersions.Family(tool, latest.Version);
        var changesConfig = manifest is not null && relation == GameToolVersionRelation.UpdateAvailable
                                                 && manifest.Family.Length > 0 && latestFamily.Length > 0
                                                 && manifest.Family != latestFamily;
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
                    GameToolVersionRelation.UpdateAvailable => "Version " + latest!.Version + " is available.",
                    GameToolVersionRelation.NewerThanLatest => "Newer than the latest stable release.",
                    _ => "Installed as " + manifest.ProxyName + "."
                };
                if (relation == GameToolVersionRelation.UpdateAvailable)
                {
                    actions.Add("update");
                }

                actions.AddRange(["configure", "repair", "remove"]);
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
            canEdit || manifest is not null && folderReady, canEdit);
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
            GameRuntimeRole.Game => "Game",
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

        var target = Clean(SteamLaunchConfig.OriginalsFrom(true, details).Target);
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
