// Shared between WSGM, WSGM.Launch and WSGM.PackagedLaunch (linked as a source file).

using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Builds launch options the way Windows programs split their command line.</summary>
internal static class LaunchArguments
{
    internal static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WindowsCommandLine.Quote(value);
    }

    internal static string Named(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return name + WindowsCommandLine.Quote(value, true);
    }

    internal static string Join(IEnumerable<string> arguments)
    {
        return string.Join(' ', arguments.Select(Quote));
    }
}
