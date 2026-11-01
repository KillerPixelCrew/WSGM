namespace WSGM.Shell;

/// <summary>What the artwork stage knows about one title.</summary>
internal enum GameLibraryArtworkStatus
{
    /// <summary>Waiting its turn.</summary>
    Pending,

    /// <summary>Being fetched now.</summary>
    Loading,

    /// <summary>Images were found.</summary>
    Ready,

    /// <summary>Every provider that could be asked was asked, and none had images for it.</summary>
    NotFound,

    /// <summary>A provider could not be asked: a rejected key, a spent quota, no connection.</summary>
    Failed,

    /// <summary>No provider is set up, so none was asked.</summary>
    Unavailable
}
