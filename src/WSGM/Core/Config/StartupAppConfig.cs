namespace WSGM.Core;

/// <summary>Describes one optional program started as part of a shell session.</summary>
public sealed class StartupAppConfig
{
    /// <summary>Executable path or protocol URL to launch.</summary>
    public string Path { get; set; } = "";

    /// <summary>Command-line arguments passed to an executable target.</summary>
    public string Args { get; set; } = "";

    /// <summary>Whether this entry participates in the shell startup sequence.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the executable must inherit WSGM's elevated token.</summary>
    public bool Elevated { get; set; }

    /// <summary>
    ///     Relaunch this tool automatically when its process dies (e.g. a
    ///     crashed Handheld Companion leaves the device without controller input).
    /// </summary>
    public bool AutoRelaunch { get; set; }
}
