using System;

namespace WSGM.Core;

/// <summary>The import records could not be read or written; nothing was changed on their account.</summary>
/// <param name="message">What went wrong, in words the page shows.</param>
/// <param name="inner">The underlying failure.</param>
public sealed class ImportStateException(string message, Exception inner) : Exception(message, inner);
