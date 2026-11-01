using System;

namespace WSGM.Core;

/// <summary>A log upload that did not finish, with the reason in words the user can act on.</summary>
/// <remarks>
///     The message also says what happened to anything already uploaded: an unfinished upload removes its bin, so
///     nothing the user did not get a link for stays readable.
/// </remarks>
internal sealed class LogUploadException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="message">The user-facing reason.</param>
    /// <param name="cancelled">Whether the user cancelled rather than the upload failing.</param>
    /// <param name="inner">The underlying failure.</param>
    internal LogUploadException(string message, bool cancelled = false, Exception? inner = null)
        : base(message, inner)
    {
        Cancelled = cancelled;
    }

    /// <summary>Gets whether the user cancelled rather than the upload failing.</summary>
    internal bool Cancelled { get; }
}
