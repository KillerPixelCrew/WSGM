namespace WSGM.Core;

/// <summary>What the desktop returns to when Game Mode ends.</summary>
public enum GameModeReturn
{
    /// <summary>Whatever was on screen when Game Mode was entered.</summary>
    EntryArrangement,

    /// <summary>A saved desktop layout, regardless of what entry found.</summary>
    DesktopLayout
}
