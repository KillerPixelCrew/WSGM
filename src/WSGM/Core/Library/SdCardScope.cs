namespace WSGM.Core;

/// <summary>Which card(s) a <see cref="FilterKind.SdCard" /> filter matches.</summary>
public enum SdCardScope
{
    /// <summary>The card currently inserted.</summary>
    Inserted,

    /// <summary>Any card WSGM tracks.</summary>
    Any,

    /// <summary>One specific card, by content id (<see cref="FilterNode.ContentId" />).</summary>
    Specific
}
