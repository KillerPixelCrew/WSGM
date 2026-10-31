namespace WSGM.Shell;

/// <summary>The <see cref="GameLibraryState.Phase" /> values, shared by the service and the overlay.</summary>
public static class GameLibraryPhases
{
    /// <summary>Nothing has been scanned yet.</summary>
    public const string Idle = "idle";

    /// <summary>A scan is running.</summary>
    public const string Scanning = "scanning";

    /// <summary>A scan finished and its entries can be reviewed.</summary>
    public const string Review = "review";

    /// <summary>An apply is running.</summary>
    public const string Applying = "applying";

    /// <summary>An apply finished.</summary>
    public const string Done = "done";
}
