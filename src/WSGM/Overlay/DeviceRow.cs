using System.Linq;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>One row of a <see cref="DeviceGroup" />, keyed by its stable, unscoped focus key.</summary>
/// <param name="Key">The row's focus key; a pinned copy prefixes it with <c>pin:</c> when it is created.</param>
/// <remarks>
///     A row whose layout matches the drawn one keeps its control and only refreshes its value, so focus and
///     drafts survive; any layout change rebuilds the group that holds it.
/// </remarks>
internal abstract record DeviceRow(string Key)
{
    /// <summary>Whether the row counts on the section rail; captions and previews do not.</summary>
    internal virtual bool Counts => true;

    /// <summary>Whether a control drawn for <paramref name="drawn" /> can show this row by refreshing its value.</summary>
    /// <param name="drawn">The row the control was built from.</param>
    /// <returns>True when kind, key and every layout property match; values never take part.</returns>
    internal abstract bool SameLayoutAs(DeviceRow drawn);

    /// <summary>Whether a descriptor control can show <paramref name="next" /> by refreshing its value.</summary>
    /// <remarks>
    ///     Whether a row can be invoked decides between an action button and a reading only for a row
    ///     without an editor; a choice or slider just enables or disables.
    /// </remarks>
    private static bool SameDescriptorLayout(DescriptorRow next, DescriptorRow drawn, bool choice)
    {
        return next.Id == drawn.Id && next.Title == drawn.Title && next.Range == drawn.Range
               && next.Options.SequenceEqual(drawn.Options)
               && (choice || next.Range is not null || next.Options.Count > 0 || next.CanInvoke == drawn.CanInvoke);
    }

    /// <summary>A device or graphics capability.</summary>
    /// <param name="Value">The published row.</param>
    /// <param name="Graphics">Whether it writes to the graphics source rather than the device.</param>
    /// <param name="Marker">The temperature marker for a curve row, if any.</param>
    internal sealed record Capability(DeviceOverlayCapability Value, bool Graphics, int? Marker = null)
        : DeviceRow(Graphics
            ? "graphics." + Value.GpuPluginId + "/" + DeviceOverlayGroups.RowKey(Value)
            : DeviceOverlayGroups.RowKey(Value))
    {
        /// <summary>The minimum width of a reading, or null for a full-width row.</summary>
        /// <remarks>Compact readings share a line more tightly; primary ones and anything that writes span the row.</remarks>
        internal double? ReadingWidth =>
            !Value.Writable && !Value.SupportsAction && Value.ValueKind != CapabilityValueKind.None
            && Value.Prominence != CapabilityProminence.Primary
                ? Value.Prominence == CapabilityProminence.Compact ? 112 : 160
                : null;

        /// <inheritdoc />
        internal override bool SameLayoutAs(DeviceRow drawn)
        {
            return drawn is Capability other && other.Graphics == Graphics && Key == other.Key
                   && Value.SameLayoutAs(other.Value);
        }
    }

    /// <summary>A host-owned Device row, drawn as its selection's choice when it has one.</summary>
    /// <param name="Value">The published row, passed on unchanged.</param>
    /// <param name="Selection">The host selection keyed by the row's id, or null.</param>
    internal sealed record Host(DescriptorRow Value, DeviceHostSelection? Selection) : DeviceRow(Value.Id)
    {
        /// <inheritdoc />
        internal override bool SameLayoutAs(DeviceRow drawn)
        {
            return drawn is Host other && SameDescriptorLayout(Value, other.Value, Selection is not null)
                                       && Selection is null == other.Selection is null
                                       && (Selection is null ||
                                           Selection.Choices.SequenceEqual(other.Selection!.Choices));
        }
    }

    /// <summary>A shared performance row.</summary>
    /// <param name="Value">The published row.</param>
    /// <param name="RowKey">Its focus key on this placement.</param>
    internal sealed record Performance(DescriptorRow Value, string RowKey) : DeviceRow(RowKey)
    {
        /// <inheritdoc />
        internal override bool SameLayoutAs(DeviceRow drawn)
        {
            return drawn is Performance other && Key == other.Key && SameDescriptorLayout(Value, other.Value, false);
        }
    }

    /// <summary>A composite WSGM view, such as the power assignments or the Windows energy plan.</summary>
    /// <param name="Id">The view's pin id, which also names its factory.</param>
    internal sealed record View(string Id) : DeviceRow(Id)
    {
        /// <inheritdoc />
        internal override bool SameLayoutAs(DeviceRow drawn)
        {
            return drawn is View other && Key == other.Key;
        }
    }

    /// <summary>A caption explaining why a control is absent.</summary>
    /// <param name="TextKey">The caption's key.</param>
    /// <param name="Value">The caption.</param>
    internal sealed record Text(string TextKey, string Value) : DeviceRow(TextKey)
    {
        /// <inheritdoc />
        internal override bool Counts => false;

        /// <inheritdoc />
        internal override bool SameLayoutAs(DeviceRow drawn)
        {
            return drawn is Text other && Key == other.Key && Value == other.Value;
        }
    }

    /// <summary>The button glyph preview and input test, drawn after the glyph selection it results from.</summary>
    /// <param name="Value">The resolved preview.</param>
    internal sealed record GlyphPreview(DeviceOverlayGlyphPreview Value) : DeviceRow("device.glyph-preview")
    {
        /// <inheritdoc />
        internal override bool Counts => false;

        /// <inheritdoc />
        internal override bool SameLayoutAs(DeviceRow drawn)
        {
            return drawn is GlyphPreview other && Value.ProfileName == other.Value.ProfileName
                                               && Value.Detail == other.Value.Detail
                                               && Value.InputTestAvailable == other.Value.InputTestAvailable;
        }
    }
}
