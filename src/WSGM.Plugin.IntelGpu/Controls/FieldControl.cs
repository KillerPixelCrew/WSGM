using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Controls;

/// <summary>One field of a driver structure that several controls share, read once per pass.</summary>
/// <typeparam name="T">The IGCL structure.</typeparam>
/// <remarks>
///     The structure's source reads it once per pass, so the rows of one structure (sharpness on, filter
///     and intensity, for example) cost one driver call together. A row is its descriptor and two
///     functions: how to read its field out of the structure and how to build the request that writes it.
/// </remarks>
internal class FieldControl<T> : IntelControl
    where T : unmanaged
{
    /// <summary>Reads a row's value out of the structure.</summary>
    /// <param name="control">The row.</param>
    /// <param name="value">What the driver returned.</param>
    /// <returns>The read.</returns>
    public delegate ControlRead Decoder(FieldControl<T> control, T value);

    /// <summary>Builds the request that writes a row's value.</summary>
    /// <param name="control">The row.</param>
    /// <param name="current">The structure as last read, when the row carries; default otherwise.</param>
    /// <param name="known">Whether that read succeeded.</param>
    /// <param name="value">A value the row validated.</param>
    /// <returns>The whole request.</returns>
    public delegate T Encoder(FieldControl<T> control, T current, bool known, CapabilityValue value);

    private readonly bool _carry;
    private readonly Decoder _decode;
    private readonly Encoder _encode;

    /// <summary>Builds one row.</summary>
    /// <param name="source">The structure.</param>
    /// <param name="descriptor">The row's descriptor.</param>
    /// <param name="decode">Reads the row's value out of a structure the driver returned.</param>
    /// <param name="encode">Builds the request that writes a value.</param>
    /// <param name="carry">
    ///     Whether the request carries the structure's other fields, so a write first reads the current
    ///     structure. The read fills those fields; it never decides whether to write.
    /// </param>
    /// <param name="members">The offered members of a choice.</param>
    /// <param name="range">The range of a ranged row, when it is not the descriptor's plain one.</param>
    public FieldControl(
        IgclSource<T> source,
        CapabilityDescriptor descriptor,
        Decoder decode,
        Encoder encode,
        bool carry,
        IReadOnlyList<EnumMember>? members = null,
        IntegerRange? range = null)
        : base(descriptor, members, range)
    {
        Source = source;
        _decode = decode;
        _encode = encode;
        _carry = carry;
        SupportKey = source.Claim(Key);
    }

    /// <summary>The structure the row belongs to.</summary>
    protected IgclSource<T> Source { get; }

    /// <inheritdoc />
    public override string SupportKey { get; }

    /// <inheritdoc />
    public override ControlRead Read()
    {
        var result = Source.Read(out var value);
        return result == IgclResult.Success ? _decode(this, value) : ControlRead.Driver(result);
    }

    /// <inheritdoc />
    public override ControlWrite ProbeSupport(WriteAdmission admission)
    {
        return Source.ProbeSupport(admission);
    }

    /// <inheritdoc />
    protected override ControlWrite WriteValidated(CapabilityValue value, WriteAdmission admission)
    {
        return ControlWrite.From(Source.Write(Encode(value), admission),
            Descriptor.Display.CustomLabel ?? CapabilityId);
    }

    /// <summary>Builds the request for a validated value, reading the structure first when the row carries.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The request.</returns>
    protected T Encode(CapabilityValue value)
    {
        T current = default;
        var known = _carry && Source.Read(out current) == IgclResult.Success;
        return _encode(this, current, known, value);
    }
}
