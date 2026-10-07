using System;

namespace WSGM.Input;

/// <summary>Shared numeric scaling for the controller wire formats.</summary>
internal static class WireScale
{
    // Avoid -32768: SDL's Deck Y-axis negation would wrap it back to itself.
    /// <summary>Scales a finite signed unit axis to the symmetric wire range.</summary>
    /// <param name="value">Finite normalized axis; out-of-range values clamp.</param>
    /// <returns>-32767 through 32767, avoiding the negation overflow of -32768.</returns>
    internal static short Axis16(float value)
    {
        return (short)Math.Clamp(MathF.Round(value * short.MaxValue), short.MinValue + 1, short.MaxValue);
    }

    /// <summary>Scales a finite unsigned unit input to one wire byte.</summary>
    /// <param name="value">Finite normalized trigger value; out-of-range values clamp.</param>
    /// <returns>Zero through 255 after rounding.</returns>
    internal static byte Trigger8(float value)
    {
        return (byte)Math.Clamp(MathF.Round(value * byte.MaxValue), 0, byte.MaxValue);
    }

    /// <summary>Scales finite physical motion to a signed 16-bit wire field.</summary>
    /// <param name="value">Finite angular velocity or acceleration.</param>
    /// <param name="scale">Protocol counts per physical unit.</param>
    /// <returns>Rounded counts saturated to the full signed 16-bit range.</returns>
    internal static short Motion16(float value, float scale)
    {
        return (short)Math.Clamp(MathF.Round(value * scale), short.MinValue, short.MaxValue);
    }
}
