using System;

namespace WSGM.Core;

/// <summary>A Notes file could not be read or written; nothing was changed on its account.</summary>
/// <param name="message">What went wrong, in words a page can show.</param>
/// <param name="inner">The underlying failure.</param>
public sealed class NoteStoreException(string message, Exception? inner = null) : Exception(message, inner);
