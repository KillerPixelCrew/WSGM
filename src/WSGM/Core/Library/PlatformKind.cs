namespace WSGM.Core;

/// <summary>Steam app vs non-Steam shortcut, for <see cref="FilterKind.Platform" />.</summary>
public enum PlatformKind
{
    /// <summary>A real Steam app.</summary>
    Steam,

    /// <summary>A non-Steam shortcut.</summary>
    NonSteam
}
