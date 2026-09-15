using System;

namespace WSGM.Overlay;

/// <summary>Tiny fluent helper so builders can configure-and-return in one expression.</summary>
internal static class FluentExtensions
{
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
