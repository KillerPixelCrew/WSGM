namespace WSGM.Shell;

/// <summary>One saved preset as both surfaces list it for management.</summary>
/// <param name="Id">The preset's identifier, which commands name.</param>
/// <param name="Name">The name the user gave it.</param>
/// <param name="Detail">Its values in one line, and why it cannot be applied when it does not fit.</param>
/// <param name="Fits">Whether its values fit the device's current limits.</param>
/// <param name="Active">Whether the current reading is this preset.</param>
internal sealed record PowerPresetSavedRow(string Id, string Name, string Detail, bool Fits, bool Active);
