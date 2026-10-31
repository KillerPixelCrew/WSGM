namespace WSGM.Shell;

/// <summary>Projects the shared library backend for overlay consumers; the session owns its lifetime.</summary>
internal interface IGameLibraryOverlaySource : IGameLibraryBackend, IChangeSource
{
    /// <summary>Reads the shared library’s latest published review.</summary>
    /// <returns>A snapshot for rendering; no scan or Steam query is started.</returns>
    GameLibraryState ReadState();

    /// <summary>Reads evidence already gathered for one review entry.</summary>
    /// <param name="id">Stable review entry identifier.</param>
    /// <returns>Entry details, or null when the entry is no longer listed.</returns>
    GameLibraryDetails? ReadDetails(string id);

    /// <summary>Reads cached candidates for one review entry’s artwork slot.</summary>
    /// <param name="id">Stable review entry identifier.</param>
    /// <param name="asset">Artwork slot: grid, wide, hero, logo or icon.</param>
    /// <returns>Candidates and loading status, or null when the entry or slot is unavailable.</returns>
    GameLibraryOptionsAnswer? ReadArtworkOptions(string id, string asset);
}
