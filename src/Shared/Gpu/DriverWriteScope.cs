// SPDX-License-Identifier: MIT

namespace WSGM.Plugin.Gpu;

/// <summary>Revalidates admission immediately before native setters, after any preparatory reads.</summary>
internal static class DriverWriteScope
{
    [ThreadStatic] private static Action? _check;

    internal static T Run<T>(Func<T> work, Action check)
    {
        var previous = _check;
        _check = check;
        try
        {
            return work();
        }
        finally
        {
            _check = previous;
        }
    }

    internal static void Check()
    {
        (_check ?? throw new DriverFailure("No active GPU write admission."))();
    }
}
