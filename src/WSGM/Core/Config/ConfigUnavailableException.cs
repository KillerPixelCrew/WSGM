using System;
using System.IO;

namespace WSGM.Core;

/// <summary>A strict operation could not safely obtain or publish configuration.</summary>
public sealed class ConfigUnavailableException : IOException
{
    /// <summary>Creates a failure preserving its original cause.</summary>
    /// <param name="message">What operation failed.</param>
    /// <param name="inner">The original failure.</param>
    public ConfigUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
