namespace WSGM.Core;

/// <summary>One of the six user-facing idle timeouts of the active power scheme.</summary>
public enum PowerTimeoutKind
{
    /// <summary>Turn off the display after (on battery).</summary>
    DisplayDc,

    /// <summary>Turn off the display after (plugged in).</summary>
    DisplayAc,

    /// <summary>Go to standby after (on battery).</summary>
    SleepDc,

    /// <summary>Go to standby after (plugged in).</summary>
    SleepAc,

    /// <summary>Hibernate after (on battery); Modern Standby counts it from standby entry.</summary>
    HibernateDc,

    /// <summary>Hibernate after (plugged in); Modern Standby counts it from standby entry.</summary>
    HibernateAc
}
