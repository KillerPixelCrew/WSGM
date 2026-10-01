using System;

namespace WSGM.Setup.Engine;

/// <summary>The physical setup image, including a retained image renamed during self recovery.</summary>
internal static class SetupExecutable
{
    internal static string Path { get; set; } = Environment.ProcessPath
                                                ?? throw new InvalidOperationException(
                                                    "The executing setup path is unavailable.");
}
