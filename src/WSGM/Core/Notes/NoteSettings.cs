using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Notes preferences and display names kept beside the pages.</summary>
public sealed class NoteSettings
{
    /// <summary>The settings format this build writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Shortest selectable screenshot countdown, in seconds.</summary>
    public const int MinimumCountdown = 5;

    /// <summary>Longest selectable screenshot countdown, in seconds.</summary>
    public const int MaximumCountdown = 10;

    /// <summary>The format the file was written in.</summary>
    public int Format { get; set; } = CurrentFormat;

    /// <summary>The countdown the next screenshot offers first, in seconds.</summary>
    public int CaptureCountdownSeconds { get; set; } = 7;

    /// <summary>The last known display name of each game notebook, by scope.</summary>
    /// <remarks>Kept so a notebook stays readable after its game is uninstalled or Steam is unavailable.</remarks>
    public Dictionary<string, string> NotebookTitles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Brings a countdown into the selectable range.</summary>
    /// <param name="seconds">The requested countdown, in seconds.</param>
    /// <returns>The countdown, from <see cref="MinimumCountdown" /> to <see cref="MaximumCountdown" />.</returns>
    public static int ClampCountdown(int seconds)
    {
        return Math.Clamp(seconds, MinimumCountdown, MaximumCountdown);
    }
}
