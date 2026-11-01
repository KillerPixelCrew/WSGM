namespace WSGM.Shell;

/// <summary>Truthful availability of the canonical running-application target.</summary>
internal enum RunningApplicationTargetState
{
    Global,
    Active,
    IdentityOnly,
    Ambiguous,
    Unavailable
}
