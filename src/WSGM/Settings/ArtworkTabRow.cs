using WSGM.Core;

namespace WSGM.Settings;

/// <summary>One tab of the in-Steam artwork page, as Settings edits it.</summary>
/// <remarks>
///     The order of these rows is the tab order, so moving a row is the whole edit. The ids are
///     fixed by <see cref="ArtworkConfig.DefaultTabOrder" /> and never come from the user.
/// </remarks>
public sealed class ArtworkTabRow : ObservableObject
{
    /// <summary>Creates a row for one tab.</summary>
    /// <param name="id">The tab id stored in configuration.</param>
    /// <param name="title">What to call it.</param>
    /// <param name="visible">Whether the tab is offered.</param>
    public ArtworkTabRow(string id, string title, bool visible)
    {
        Id = id;
        Title = title;
        Visible = visible;
    }

    /// <summary>Gets the tab id as configuration stores it.</summary>
    public string Id { get; }

    /// <summary>Gets what the tab is called.</summary>
    public string Title { get; }

    /// <summary>Gets or sets whether the artwork page offers this tab.</summary>
    public bool Visible
    {
        get;
        set => SetField(ref field, value, nameof(Visible));
    }
}
