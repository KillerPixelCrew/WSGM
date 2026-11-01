namespace WSGM.Core;

/// <summary>Whether a source belongs to this user or to the machine.</summary>
public enum SteamAutostartScope
{
    /// <summary>Per-user; WSGM can change it without elevation.</summary>
    User,

    /// <summary>Machine-wide or a scheduled task; changing it needs elevation.</summary>
    Machine
}
