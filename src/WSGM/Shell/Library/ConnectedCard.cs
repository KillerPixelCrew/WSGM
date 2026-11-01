using WSGM.Core;

namespace WSGM.Shell;

/// <summary>A connected card's library manifest and the volume it was read through.</summary>
/// <param name="Manifest">The manifest as read in this pass.</param>
/// <param name="Volume">The volume it was read from.</param>
internal sealed record ConnectedCard(StorageLibraryManifest Manifest, ObservedVolume Volume);
