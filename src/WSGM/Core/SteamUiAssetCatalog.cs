using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WSGM.Core;

/// <summary>Hash-locked repository-owned JavaScript assets for the Steam UI host.</summary>
public static class SteamUiAssetCatalog
{
    /// <summary>Embedded resource name of the version-one native-QAM bootstrap.</summary>
    public const string NativeQamBootstrapResource =
        "WSGM.Core.SteamUiAssets.NativeQamBootstrap.js";

    /// <summary>Expected SHA-256 of the UTF-8 bootstrap source.</summary>
    public const string NativeQamBootstrapSha256 =
        "D16AAE70A668899EF59C209A981B89270A79DF37C3018CADAC9CB96F5BB85269";

    /// <summary>Loads and verifies the embedded native-QAM bootstrap.</summary>
    /// <returns>The exact repository-owned JavaScript source.</returns>
    public static string LoadNativeQamBootstrap()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(NativeQamBootstrapResource)
            ?? throw new InvalidDataException("Embedded Steam UI bootstrap is missing.");
        using var reader = new StreamReader(
            stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var source = reader.ReadToEnd();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        if (!string.Equals(hash, NativeQamBootstrapSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Embedded Steam UI bootstrap hash did not match source.");
        }
        return source;
    }
}
