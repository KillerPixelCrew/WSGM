using System;

namespace WSGM.Input;

/// <summary>Shared numeric scaling for the controller wire formats.</summary>
internal static class WireScale
{
    // Avoid -32768: SDL's Deck Y-axis negation would wrap it back to itself.
    internal static short Axis16(float value)
    {
        return (short)Math.Clamp(MathF.Round(value * short.MaxValue), short.MinValue + 1, short.MaxValue);
    }

    internal static byte Trigger8(float value)
    {
        return (byte)Math.Clamp(MathF.Round(value * byte.MaxValue), 0, byte.MaxValue);
    }

    internal static short Motion16(float value, float scale)
    {
        return (short)Math.Clamp(MathF.Round(value * scale), short.MinValue, short.MaxValue);
    }
}
