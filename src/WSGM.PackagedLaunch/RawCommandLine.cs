using System;

namespace WSGM.PackagedLaunch;

/// <summary>Reads a process's own command line the way Windows ends its program name.</summary>
/// <remarks>
///     Follow mode hands another program's arguments on exactly as a shortcut wrote them, which the
///     argument array Windows split has already lost. So the raw line is cut after this launcher's
///     own path and nothing else is changed.
/// </remarks>
public static class RawCommandLine
{
    /// <summary>Everything after the program's own path in a raw command line.</summary>
    /// <param name="commandLine">The command line as Windows passed it.</param>
    /// <returns>The arguments, verbatim apart from the whitespace that separates them from the path.</returns>
    /// <remarks>
    ///     The program name ends where Windows ends it: at the closing quote when it starts with one,
    ///     otherwise at the first space or tab.
    /// </remarks>
    public static string Arguments(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var line = commandLine.TrimStart(' ', '\t');
        if (line.StartsWith('"'))
        {
            var close = line.IndexOf('"', 1);
            return close < 0 ? string.Empty : line[(close + 1)..].TrimStart(' ', '\t');
        }

        var end = line.IndexOfAny([' ', '\t']);
        return end < 0 ? string.Empty : line[(end + 1)..].TrimStart(' ', '\t');
    }
}
