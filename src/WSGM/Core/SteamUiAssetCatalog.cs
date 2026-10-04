using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SteamUiToolkit;

namespace WSGM.Core;

/// <summary>Repository-owned JavaScript assets for the Steam UI host.</summary>
internal static class SteamUiAssetCatalog
{
    /// <summary>Embedded resource name of the version-one native-QAM bootstrap.</summary>
    private const string NativeQamBootstrapResource =
        "WSGM.Core.SteamUiAssets.NativeQamBootstrap.js";

    /// <summary>Loads the embedded native-QAM bootstrap and hashes the bytes it was embedded as.</summary>
    /// <returns>The exact repository-owned JavaScript source and its uppercase SHA-256.</returns>
    /// <remarks>
    ///     The hash identifies this build's copy to the bridge, which replaces a script a previous build
    ///     left running. Whether the embedded file is current with its TypeScript source is the build's
    ///     check (npm run steam-assets:check), not a runtime one.
    /// </remarks>
    public static SteamUiInjectedAsset LoadNativeQamBootstrap()
    {
        using var stream = Assembly.GetExecutingAssembly()
                               .GetManifestResourceStream(NativeQamBootstrapResource)
                           ?? throw new InvalidDataException("Embedded Steam UI bootstrap is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        return new SteamUiInjectedAsset(
            new UTF8Encoding(false, true).GetString(bytes),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
