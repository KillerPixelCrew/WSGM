namespace WSGM.Core;

/// <summary>How Game Mode arranges displays when it starts.</summary>
public enum GameModeLaunchKind
{
    /// <summary>Start on the display Windows already calls primary, adjusting scaling only.</summary>
    Default,

    /// <summary>Apply a saved layout, optionally after waiting for a display and running actions.</summary>
    Custom
}
