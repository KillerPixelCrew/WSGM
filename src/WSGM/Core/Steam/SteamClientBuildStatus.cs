namespace WSGM.Core;

/// <summary>Whether the running Steam client is a build this WSGM was checked with.</summary>
internal enum SteamClientBuildStatus
{
    /// <summary>Its channel and version are in the shipped list.</summary>
    Checked,

    /// <summary>Its channel and version are not in the list: Steam updated past what was checked.</summary>
    Unchecked,

    /// <summary>The client's build could not be read, so nothing is claimed about it.</summary>
    Unreadable
}
