namespace WSGM.Core;

/// <summary>The colour a badge takes on the Plugins page.</summary>
public enum PluginBadgeTone
{
    /// <summary>Facts with no judgement: version, kind, a local build.</summary>
    Neutral,

    /// <summary>First-party.</summary>
    Accent,

    /// <summary>Community.</summary>
    Community,

    /// <summary>Available to install.</summary>
    Info,

    /// <summary>Installed, hardware-tested.</summary>
    Good,

    /// <summary>Blind, or being removed.</summary>
    Warn,

    /// <summary>Refused or outdated.</summary>
    Bad
}
