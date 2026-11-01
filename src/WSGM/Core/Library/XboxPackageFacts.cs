using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What one package's metadata says about the application being classified.</summary>
/// <param name="ApplicationId">The application id inside the package, or empty when absent.</param>
/// <param name="EntryPoint">Its declared entry point, or empty.</param>
/// <param name="Executable">The executable the manifest names, or empty.</param>
/// <param name="HasRunFullTrust">Whether the package declares the full-trust capability.</param>
/// <param name="ApplicationCount">How many applications the package declares.</param>
/// <param name="PackageDependencies">The package families it depends on.</param>
/// <param name="HasMicrosoftGameConfig">Whether the install root carries MicrosoftGame.config.</param>
/// <param name="ManifestReadable">Whether the manifest could be read and parsed at all.</param>
/// <param name="ProcessorArchitecture">What the package's identity declares it is built for, or empty.</param>
public sealed record XboxPackageFacts(
    string ApplicationId,
    string EntryPoint,
    string Executable,
    bool HasRunFullTrust,
    int ApplicationCount,
    IReadOnlyList<string> PackageDependencies,
    bool HasMicrosoftGameConfig,
    bool ManifestReadable = true,
    string ProcessorArchitecture = "");
