namespace WSGM.Shell;

/// <summary>One entry WSGM added to HidHide, remembered so exit and uninstall take exactly that back out.</summary>
internal sealed class HidHideOwnedDelta
{
    public HidHideEntryKind EntryKind { get; init; }

    public string Value { get; init; } = string.Empty;
}
