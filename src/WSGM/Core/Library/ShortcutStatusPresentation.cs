namespace WSGM.Core;

/// <summary>The one wording of the three availability states, shared by both surfaces and Steam.</summary>
public static class ShortcutStatusPresentation
{
    /// <summary>The short label for a state.</summary>
    public static string Label(ContentAvailability state)
    {
        return state switch
        {
            ContentAvailability.Available => "Available",
            ContentAvailability.LibraryMissing => "Unavailable · library missing",
            _ => "Unavailable · files missing"
        };
    }
}
