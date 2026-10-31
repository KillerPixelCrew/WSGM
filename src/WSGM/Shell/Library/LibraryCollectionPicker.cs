using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The collections, as last read from Steam.</summary>
/// <param name="Items">The collections, empty before the first read and after a failed one.</param>
/// <param name="Loaded">Whether a read succeeded at least once.</param>
/// <param name="Loading">Whether a read is in flight.</param>
/// <param name="Error">Why the last read failed, or null.</param>
internal sealed record LibraryCollectionPicker(
    IReadOnlyList<LibraryCollectionChoice> Items, bool Loaded, bool Loading, string? Error);
