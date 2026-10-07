using System;

namespace WSGM.Overlay;

/// <summary>Tiny fluent helper so builders can configure-and-return in one expression.</summary>
internal static class FluentExtensions
{
    /// <summary>Applies synchronous configuration and returns the same value.</summary>
    /// <typeparam name="T">Value being configured.</typeparam>
    /// <param name="value">Object or value passed unchanged to the callback.</param>
    /// <param name="configure">Callback invoked once; exceptions propagate.</param>
    /// <returns>The input value.</returns>
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
