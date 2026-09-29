namespace WSGM.Plugin.IntelGpu.Controls;

/// <summary>
///     An integer range standing for a driver range, with the scale that maps one onto the other.
/// </summary>
/// <param name="Minimum">Inclusive minimum the descriptor offers.</param>
/// <param name="Maximum">Inclusive maximum.</param>
/// <param name="Step">Step between legal values.</param>
/// <param name="Scale">Integer units per driver unit: 1, 10, 100 or 1000.</param>
/// <param name="OffAtZero">
///     Whether zero stands for the feature's Enable flag off, below the driver's own minimum.
/// </param>
/// <param name="NativeMinimum">The driver's minimum in integer units, for writes.</param>
internal readonly record struct IntegerRange(
    int Minimum,
    int Maximum,
    int Step,
    int Scale,
    bool OffAtZero,
    int NativeMinimum)
{
    /// <summary>Converts a driver value into the published integer.</summary>
    /// <param name="value">The driver value.</param>
    /// <returns>The integer, clamped into the offered range.</returns>
    public int ToInteger(double value)
    {
        var scaled = Math.Round(value * Scale, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(scaled, OffAtZero ? NativeMinimum : Minimum, Maximum);
    }

    /// <summary>Converts a published integer into the driver value.</summary>
    /// <param name="value">The integer.</param>
    /// <returns>The driver value.</returns>
    public double ToNative(int value)
    {
        return value / (double)Scale;
    }

    /// <summary>Whether a requested integer is legal.</summary>
    /// <param name="value">The request.</param>
    /// <returns><see langword="true" /> inside the range and on a step.</returns>
    public bool Accepts(int value)
    {
        if (OffAtZero && value == 0)
        {
            return true;
        }

        var floor = OffAtZero ? NativeMinimum : Minimum;
        return value >= floor && value <= Maximum && (value - floor) % Step == 0;
    }
}

/// <summary>Maps driver ranges and enums onto WSGM's integer and choice shapes.</summary>
internal static class ValueMapping
{
    /// <summary>The finest scale a float range is published at.</summary>
    private const int MaxScale = 1000;

    /// <summary>Builds the integer range for a float range.</summary>
    /// <param name="minimum">Driver minimum.</param>
    /// <param name="maximum">Driver maximum.</param>
    /// <param name="step">Driver step; zero or negative means continuous.</param>
    /// <param name="offAtZero">Whether zero stands for Enable off.</param>
    /// <returns>The range, or null when the driver's figures are not usable.</returns>
    /// <remarks>
    ///     The scale is the smallest power of ten, up to 1000, that makes the step a whole number. A
    ///     continuous range steps in hundredths. The published number is therefore the driver value times
    ///     the scale, which a sharpness range of 0-1 in steps of 0.1 publishes as 0-10.
    /// </remarks>
    public static IntegerRange? FromFloat(float minimum, float maximum, float step, bool offAtZero)
    {
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum) || maximum < minimum)
        {
            return null;
        }

        var effectiveStep = step > 0 && float.IsFinite(step) ? step : 0.01;
        var scale = 1;
        while (scale < MaxScale && Math.Abs(effectiveStep * scale - Math.Round(effectiveStep * scale)) > 1e-4)
        {
            scale *= 10;
        }

        var integerStep = Math.Max(1, (int)Math.Round(effectiveStep * scale));
        return Build(
            Math.Round(minimum * (double)scale),
            Math.Round(maximum * (double)scale),
            integerStep,
            scale,
            offAtZero);
    }

    /// <summary>Builds the integer range for a signed range.</summary>
    /// <param name="minimum">Driver minimum.</param>
    /// <param name="maximum">Driver maximum.</param>
    /// <param name="step">Driver step.</param>
    /// <param name="offAtZero">Whether zero stands for Enable off.</param>
    /// <returns>The range, or null when unusable.</returns>
    public static IntegerRange? FromInt(int minimum, int maximum, int step, bool offAtZero)
    {
        return maximum < minimum ? null : Build(minimum, maximum, Math.Max(1, step), 1, offAtZero);
    }

    /// <summary>Builds the integer range for an unsigned range.</summary>
    /// <param name="minimum">Driver minimum.</param>
    /// <param name="maximum">Driver maximum.</param>
    /// <param name="step">Driver step.</param>
    /// <param name="offAtZero">Whether zero stands for Enable off.</param>
    /// <returns>The range, or null when unusable.</returns>
    public static IntegerRange? FromUInt(uint minimum, uint maximum, uint step, bool offAtZero)
    {
        return maximum < minimum
            ? null
            : Build(minimum, Math.Min(maximum, int.MaxValue), (int)Math.Clamp(step, 1u, int.MaxValue), 1, offAtZero);
    }

    private static IntegerRange? Build(double minimum, double maximum, int step, int scale, bool offAtZero)
    {
        if (minimum < int.MinValue || maximum > int.MaxValue || maximum < minimum)
        {
            return null;
        }

        var low = (int)minimum;
        var high = (int)maximum;

        // Zero only stands for off when the driver's range cannot contain it. A range starting at or
        // below zero keeps its own meaning for zero, and Enable is then always written true.
        var off = offAtZero && low > 0;
        var offered = off ? 0 : low;
        var offeredStep = off && low % step != 0 ? 1 : step;
        return high == offered ? null : new IntegerRange(offered, high, offeredStep, scale, off, low);
    }

    /// <summary>Decodes the three-letter EDID manufacturer id.</summary>
    /// <param name="raw">The id as <c>DISPLAYCONFIG_TARGET_DEVICE_NAME.edidManufactureId</c> reports it.</param>
    /// <returns>The PnP id in lower case, or null when neither byte order yields letters.</returns>
    /// <remarks>
    ///     EDID stores the id big-endian in bytes 8 and 9. Windows hands over the two bytes as they sit
    ///     in the EDID, which reads little-endian in a <c>ushort</c>, so the swapped order is tried first.
    /// </remarks>
    public static string? DecodeManufacturer(ushort raw)
    {
        return Decode((ushort)((raw >> 8) | (raw << 8))) ?? Decode(raw);

        static string? Decode(ushort value)
        {
            // Bit 15 is reserved and zero in a real id, which is what tells the two orders apart.
            if ((value & 0x8000) != 0)
            {
                return null;
            }

            Span<char> letters = stackalloc char[3];
            for (var index = 0; index < 3; index++)
            {
                var code = (value >> (10 - index * 5)) & 0x1f;
                if (code is < 1 or > 26)
                {
                    return null;
                }

                letters[index] = (char)('a' + code - 1);
            }

            return new string(letters);
        }
    }

    /// <summary>FNV-1a over the lower-cased text, for a short stable fingerprint.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Eight lower-case hexadecimal digits.</returns>
    public static string Fingerprint(string text)
    {
        var hash = 2166136261u;
        foreach (var character in text.ToLowerInvariant())
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }
}
