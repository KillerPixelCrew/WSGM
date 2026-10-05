using System;
using System.Linq;
using System.Text.Json;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Transports;

/// <summary>
///     The pins compiled into this build from the embedded lock files (<c>external/pawnio/pawnio.lock.json</c>
///     and <c>external/kx/kx.lock.json</c>).
/// </summary>
internal static class LabPins
{
    private const string PawnIoLock = "WSGM.DeviceLab.PawnIO.lock.json";
    private const string KxLock = "WSGM.DeviceLab.KX.lock.json";

    /// <summary>The pinned SHA-256 of one PawnIO module.</summary>
    /// <param name="id">Module ID in the lock file.</param>
    /// <returns>The digest as the lock file writes it.</returns>
    public static string PawnIoModuleSha256(string id)
    {
        using var document = Lock(PawnIoLock);
        foreach (var module in document.RootElement.GetProperty("modules").EnumerateArray())
        {
            if (module.GetProperty("id").GetString() == id)
            {
                return module.GetProperty("memberSha256").GetString()!;
            }
        }

        throw new InvalidOperationException($"The PawnIO lock file has no {id} module.");
    }

    /// <summary>The pinned SHA-256 of KX.exe.</summary>
    /// <returns>The digest as the lock file writes it.</returns>
    public static string KxSha256()
    {
        using var document = Lock(KxLock);
        return document.RootElement.GetProperty("component").GetProperty("sha256").GetString()!;
    }

    /// <summary>The pinned PawnIO installer.</summary>
    /// <returns>The pin.</returns>
    public static PawnIoPin PawnIoInstaller()
    {
        using var document = Lock(PawnIoLock);
        var component = document.RootElement.GetProperty("component");
        return new PawnIoPin
        {
            Version = component.GetProperty("version").GetString()!,
            AssetSha256 = component.GetProperty("assetSha256").GetString()!,
            SignerThumbprint = component.GetProperty("signerThumbprint").GetString()!,
            InstallArguments =
                [.. component.GetProperty("installArguments").EnumerateArray().Select(item => item.GetString()!)],
            UninstallArguments =
                [.. component.GetProperty("uninstallArguments").EnumerateArray().Select(item => item.GetString()!)],
            UninstallKey = component.GetProperty("uninstallKey").GetString()!,
            MinimumInstalledVersion = component.GetProperty("minimumInstalledVersion").GetString()!
        };
    }

    // A missing lock file is a build defect, so no caller handles it specially.
    private static JsonDocument Lock(string resource)
    {
        using var stream = typeof(LabPins).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"The {resource} lock file is not embedded.");
        return JsonDocument.Parse(stream);
    }
}
