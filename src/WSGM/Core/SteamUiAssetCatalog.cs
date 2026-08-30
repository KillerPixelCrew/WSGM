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
        "DAD9BBFA4AF6D957BE95E3B1DC8574174F9BEE0169EC8E8B02EB49BC2013BAF4";

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
