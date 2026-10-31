namespace WSGM.Core;

/// <summary>Why a resume did or did not send the machine back to sleep.</summary>
/// <remarks>
///     Every refusal is named rather than collapsed into a bool. A machine that quietly stays awake and
///     a machine that quietly suspends are both surprising, and the difference between "you woke it"
///     and "something else did, but you are using it" is the whole of this feature's diagnosis.
/// </remarks>
internal enum ModernStandbyOutcome
{
    /// <summary>The mode is switched off. WSGM does nothing on resume.</summary>
    Disabled,

    /// <summary>Windows attributes the wake to a person. Their wake is never undone.</summary>
    UserWoke,

    /// <summary>The display came on. Something is being shown to somebody.</summary>
    DisplayOn,

    /// <summary>Input was observed since the wake.</summary>
    UserActive,

    /// <summary>This wake has already been slept through as often as WSGM will try.</summary>
    AttemptsExhausted,

    /// <summary>Still inside the grace period; ask again later.</summary>
    TooSoon,

    /// <summary>Nothing accounts for the wake. Suspend again.</summary>
    Resuspend
}
