using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Everything WSGM remembers about Game Tools for one AppID.</summary>
internal sealed record GameToolRecord
{
    /// <summary>The Steam AppID or shortcut id.</summary>
    public uint AppId { get; init; }

    /// <summary>Whether the id is a non-Steam shortcut.</summary>
    public bool IsShortcut { get; init; }

    /// <summary>The game name when last seen.</summary>
    public string Name { get; init; } = "";

    /// <summary>The confirmed runtime executable.</summary>
    public GameRuntimeChoice? Runtime { get; init; }

    /// <summary>Installed tools.</summary>
    public List<GameToolManifest> Tools { get; init; } = [];

    /// <summary>Finds a tool's manifest.</summary>
    /// <param name="toolId">The tool id.</param>
    /// <returns>The manifest, or null when WSGM has not installed the tool for this game.</returns>
    internal GameToolManifest? Manifest(string toolId)
    {
        return Tools.FirstOrDefault(manifest => manifest.ToolId == toolId);
    }

    /// <summary>Every file the manifests own below the install root, so folder scans can leave them out.</summary>
    /// <param name="root">The game's current install root.</param>
    /// <returns>Forward-slash paths relative to the root, compared ignoring case.</returns>
    internal HashSet<string> OwnedPaths(string root)
    {
        HashSet<string> owned = new(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in Tools)
        {
            var directory = GameToolPaths.Resolve(root, manifest.TargetDirectory);
            foreach (var file in manifest.Files)
            {
                var full = GameToolPaths.Resolve(directory, file.RelativePath);
                if (StoragePaths.IsUnder(root, full))
                {
                    owned.Add(GameToolPattern.Normalize(Path.GetRelativePath(root, full)));
                }
            }
        }

        return owned;
    }
}
