namespace WSGM.Shell;

/// <summary>ES-DE's setup as both surfaces show it on its detail page.</summary>
/// <param name="Installed">Whether WSGM's ES-DE is installed.</param>
/// <param name="Summary">"12 systems from 9 ROM folders", or what is missing.</param>
/// <param name="Status">What needs attention, or empty when ES-DE is in step with WSGM.</param>
/// <param name="Tone"><c>warn</c> or <c>bad</c> for <paramref name="Status" />, or empty.</param>
internal sealed record EsDeState(bool Installed, string Summary, string Status, string Tone)
{
    /// <summary>The state before ES-DE is installed.</summary>
    internal static EsDeState NotInstalled { get; } = new(false, "", "", "");

    /// <summary>WSGM's folder of links that ES-DE reads its games from.</summary>
    public string RomFolder { get; init; } = "";

    /// <summary>Whether a sync has finished since ES-DE was installed or WSGM started.</summary>
    public bool Synced { get; init; }

    /// <summary>Every system the ROM folders hold, shown in ES-DE or left out with the reason.</summary>
    public EsDeSystemRow[] Systems { get; init; } = [];

    /// <summary>Every ROM source, with its systems and its Steam choice.</summary>
    public EsDeFolderRow[] Folders { get; init; } = [];
}
