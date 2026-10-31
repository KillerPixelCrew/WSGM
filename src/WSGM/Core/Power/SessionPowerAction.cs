namespace WSGM.Core;

/// <summary>A machine power action the user asked the session for.</summary>
public enum SessionPowerAction
{
    /// <summary>Enter standby.</summary>
    Standby,

    /// <summary>Hibernate.</summary>
    Hibernate,

    /// <summary>Restart Windows.</summary>
    Restart,

    /// <summary>Shut Windows down.</summary>
    Shutdown,

    /// <summary>Sign the current Windows user out.</summary>
    SignOut
}
