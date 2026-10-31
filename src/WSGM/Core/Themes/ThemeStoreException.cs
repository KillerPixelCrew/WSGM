using System;

namespace WSGM.Core;

/// <summary>The theme store could not answer, with a message for the page.</summary>
/// <param name="message">What went wrong.</param>
public sealed class ThemeStoreException(string message) : Exception(message);
