namespace WSGM.Shell;

/// <summary>What one artwork type of one title would get.</summary>
/// <param name="Asset">The artwork type: grid, wide, hero, logo or icon.</param>
/// <param name="Kind">
///     <c>pick</c> for an image the user chose, <c>default</c> for the one a new title starts on,
///     <c>keep</c> for an imported title whose current image stays, <c>none</c> for a slot nothing
///     will be applied to, or <c>loading</c> while candidates are still being gathered.
/// </param>
/// <param name="Thumb">The image to show, or empty.</param>
/// <param name="Provider">Who supplied it, or empty.</param>
/// <param name="Index">Its position among the candidates, from one, or zero when it is not one of them.</param>
/// <param name="Count">How many candidates there are.</param>
public sealed record GameLibraryArtworkSlot(
    string Asset,
    string Kind,
    string Thumb,
    string Provider,
    int Index,
    int Count);
