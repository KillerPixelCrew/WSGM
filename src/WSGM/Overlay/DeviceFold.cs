namespace WSGM.Overlay;

/// <summary>How a <see cref="DeviceGroup" /> is framed and folded on its page.</summary>
internal enum DeviceFold
{
    /// <summary>Its own fold: the title on the fold heading, the pin action beside it, expansion kept by id.</summary>
    Folded,

    /// <summary>A heading with its pin action above open rows, inside a <see cref="Shared" /> fold.</summary>
    Open,

    /// <summary>A bordered card with a heading and pin action, inside a <see cref="Shared" /> fold.</summary>
    Card,

    /// <summary>A titled fold without a pin action that holds the page's open groups and cards.</summary>
    Shared,

    /// <summary>Rows only, without heading or frame, such as the Overview's headline toggle or a caption.</summary>
    Plain,

    /// <summary>An unframed block of nested groups, one per published GPU section.</summary>
    Section
}
