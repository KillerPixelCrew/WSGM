namespace WSGM.Shell;

/// <summary>Truthful state of WSGM's controller management for one session.</summary>
internal enum ControllerManagementState
{
    /// <summary>The user has not enabled controller management, or the release gate is closed.</summary>
    Off,

    /// <summary>Enabled, but no usable backend exists on this machine.</summary>
    Unavailable,

    /// <summary>Enabled and ready, with no virtual target present.</summary>
    Idle,

    /// <summary>A virtual target exists and canonical samples reach it.</summary>
    Active,

    /// <summary>The chosen target is the physical controller itself; Steam drives it and WSGM owns nothing.</summary>
    Native,

    /// <summary>Management faulted for this run; input falls back to SDL and the Steam lease.</summary>
    Faulted
}
