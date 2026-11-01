using System;
using System.IO;

namespace WSGM.Core;

/// <summary>A launcher's database could not be read, so nothing it lists is known.</summary>
/// <param name="message">What went wrong, naming the file.</param>
/// <param name="inner">The failure underneath.</param>
/// <remarks>
///     Thrown rather than answered with no rows: a source that found nothing reports every title it
///     imported before as uninstalled, and a locked or half-copied database is not that.
/// </remarks>
public sealed class LauncherDatabaseException(string message, Exception inner) : IOException(message, inner);
