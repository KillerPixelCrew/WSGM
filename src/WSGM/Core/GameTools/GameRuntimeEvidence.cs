using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Evidence an engine detector adds to the shared candidate scoring.</summary>
internal sealed class GameRuntimeEvidence(GameFolderLayout layout)
{
    /// <summary>The folder being resolved.</summary>
    internal GameFolderLayout Layout { get; } = layout;

    /// <summary>The detected engine id, set by the detector that recognises the layout.</summary>
    internal string Engine { get; set; } = "unknown";

    /// <summary>The detected engine label.</summary>
    internal string EngineLabel { get; set; } = "Unknown engine";

    /// <summary>Score adjustments and reasons per relative path.</summary>
    internal Dictionary<string, (int Score, GameRuntimeRole? Role, List<string> Reasons)> Marks { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a detector placed the game structurally.</summary>
    internal bool Structural { get; set; }

    /// <summary>Adds evidence for one candidate.</summary>
    /// <param name="relativePath">The candidate.</param>
    /// <param name="score">The adjustment.</param>
    /// <param name="role">A role to assign, or null to leave it.</param>
    /// <param name="reason">The user-facing reason.</param>
    internal void Mark(string relativePath, int score, GameRuntimeRole? role, string reason)
    {
        if (!Marks.TryGetValue(relativePath, out var mark))
        {
            mark = (0, null, []);
        }

        mark.Score += score;
        mark.Role = role ?? mark.Role;
        mark.Reasons.Add(reason);
        Marks[relativePath] = mark;
    }
}
