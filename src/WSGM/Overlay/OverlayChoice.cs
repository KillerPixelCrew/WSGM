using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>Retained choice editor that commits a settled selection through <see cref="ComboCommit" />.</summary>
/// <typeparam name="T">Semantic value compared with the default equality comparer.</typeparam>
internal sealed class OverlayChoice<T> : ComboBox, IOverlayRefreshable
{
    private Action<T> _commit;
    private bool _refreshing;

    /// <summary>Creates a UI-thread choice editor with the supplied value as its initial commit baseline.</summary>
    /// <param name="options">Available semantic values and visible labels.</param>
    /// <param name="current">Published value; no option is selected if it is absent from the list.</param>
    /// <param name="commit">Current owner callback for changed values; programmatic refresh does not invoke it.</param>
    internal OverlayChoice(IReadOnlyList<(T Value, string Label)> options, T current, Action<T> commit)
    {
        _commit = commit;
        ItemsSource = options.Select(option => new Option(option.Value, option.Label)).ToArray();
        SelectedItem =
            ((Option[])ItemsSource).FirstOrDefault(option => EqualityComparer<T>.Default.Equals(option.Value, current));
        ComboCommit.Attach<Option>(this, () => _refreshing, option => _commit(option.Value));
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ComboBox);

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        var next = (OverlayChoice<T>)replacement;
        _commit = next._commit;
        IsEnabled = next.IsEnabled;
        if (IsDropDownOpen)
        {
            return;
        }

        _refreshing = true;
        try
        {
            ItemsSource = next.ItemsSource;
            SelectedItem = next.SelectedItem;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private sealed record Option(T Value, string Label)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return Label;
        }
    }
}
