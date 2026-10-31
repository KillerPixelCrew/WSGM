namespace WSGM.Shell;

/// <summary>A Steam collection the Collection filter can pick.</summary>
/// <param name="Id">The collection id.</param>
/// <param name="Name">Its name.</param>
/// <param name="GameCount">How many games it holds.</param>
internal sealed record LibraryCollectionChoice(string Id, string Name, int GameCount);
