namespace WSGM;

/// <summary>The intentionally narrow operating modes accepted by the executable.</summary>
public enum RunMode
{
    /// <summary>
    ///     Runs the resident session, with Game Mode takeover or Desktop residency selected by startup options.
    ///     Explorer remains the registered Windows shell.
    /// </summary>
    Shell,

    /// <summary>Opens Settings, handing off to a resident session when available.</summary>
    Settings,

    /// <summary>Runs the manual overlay smoke-test session.</summary>
    OverlayTest
}
