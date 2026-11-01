using System;
using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The expensive part of a game's state: Steam's answer, the record and one folder walk.</summary>
/// <param name="AppId">The game.</param>
/// <param name="Name">The name.</param>
/// <param name="IsShortcut">Whether it is a non-Steam shortcut.</param>
/// <param name="InstallFolder">The current install root, empty when unknown.</param>
/// <param name="Availability">The availability value.</param>
/// <param name="Reason">Why it is not ready.</param>
/// <param name="Resolution">The runtime resolution, or null without a folder.</param>
/// <param name="Truncated">Whether the walk hit its cap.</param>
/// <param name="Upscalers">Recognised upscaler libraries.</param>
/// <param name="AntiCheat">Recognised anti-cheat systems.</param>
/// <param name="Record">The game's durable record as this scan read it, after recovery and rebasing.</param>
/// <param name="Layout">The folder walk, reused by rescans that don't need a new one; null without a folder.</param>
/// <param name="ScannedAt">When the scan finished, in <see cref="Environment.TickCount64" /> milliseconds.</param>
/// <param name="RecoveryNotice">What this scan's recovery undid, published once as the game's notice; null when nothing.</param>
internal sealed record GameToolsScan(
    uint AppId,
    string Name,
    bool IsShortcut,
    string InstallFolder,
    string Availability,
    string? Reason,
    GameRuntimeResolution? Resolution,
    bool Truncated,
    IReadOnlyList<GameUpscalerFile> Upscalers,
    IReadOnlyList<string> AntiCheat,
    GameToolRecord? Record,
    GameFolderLayout? Layout,
    long ScannedAt,
    string? RecoveryNotice = null)
{
    /// <summary>Whether the game has a folder to work in, even while it runs.</summary>
    internal bool HasFolder => GameToolStatus.HasFolder(Availability);

    /// <summary>A scan that found no usable folder.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="name">The name.</param>
    /// <param name="shortcut">Whether it is a shortcut.</param>
    /// <param name="folder">The folder Steam reported, if any.</param>
    /// <param name="availability">The availability value.</param>
    /// <param name="reason">The explanation.</param>
    /// <param name="record">The game's durable record, or null.</param>
    /// <returns>The scan.</returns>
    internal static GameToolsScan Unavailable(uint appId, string name, bool shortcut, string folder,
        string availability, string reason, GameToolRecord? record)
    {
        return new GameToolsScan(appId, name, shortcut, folder, availability, reason, null, false, [], [], record,
            null, Environment.TickCount64);
    }
}
