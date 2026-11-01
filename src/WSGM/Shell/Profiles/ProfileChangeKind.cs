namespace WSGM.Shell;

/// <summary>Why the profile snapshot changed.</summary>
internal enum ProfileChangeKind
{
    /// <summary>A different application, or a different match, is now running.</summary>
    Application,

    /// <summary>A value, a profile switch or a profile's binding changed.</summary>
    Values
}
