using System;
using WindowsDeviceControl;

namespace WSGM.Interop;

/// <summary>Native Authenticode chain and revocation verification.</summary>
/// <remarks>
///     Policy: <see cref="AuthenticodeRevocation.ChainExcludingRoot" /> (whole-chain revocation except the
///     root), verified by path with no signer expectation; the caller checks the signer it needs.
///     WindowsDeviceControl's <see cref="Authenticode" /> performs the check, which deliberately omits
///     <c>WTD_LIFETIME_SIGNING_FLAG</c>: that flag rejects a properly countersigned signature once its
///     certificate expires, and every third-party binary outlives its certificate. RTSS 7.3.5 as MSI ships
///     it is signed by Micro-Star with a certificate that expired on 2025-10-16 and is timestamped by
///     GlobalSign; Windows reports it valid, and the flag made WSGM refuse every performance control on the
///     reference Claw (2026-08-29).
/// </remarks>
internal static class NativeAuthenticode
{
    /// <summary>Verifies the embedded signature, chain, timestamp, and revocation state.</summary>
    /// <param name="path">Exact file to verify.</param>
    /// <returns>Zero on success; otherwise the WinTrust status code.</returns>
    internal static int VerifyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var result = Authenticode.Verify(path, null, AuthenticodeRevocation.ChainExcludingRoot);
        return result.TrustStatus;
    }
}
