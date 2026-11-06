using System;
using Microsoft.Win32.SafeHandles;
using WindowsDeviceControl;

namespace WSGM.Security;

/// <summary>Checks that a file carries a valid Authenticode signature from one exact certificate.</summary>
/// <remarks>
///     Trust is verified against the caller's open handle, so the result describes the bytes the caller
///     holds. Policy: <see cref="AuthenticodeRevocation.None" /> (no revocation check, cached URL retrieval
///     only), because a tester may be offline and the pinned SHA-256 already binds the installer to the
///     reviewed release, plus a pinned signer thumbprint. WindowsDeviceControl's
///     <see cref="Authenticode" /> performs the check.
/// </remarks>
internal static class AuthenticodeSignature
{
    /// <summary>Verifies the signature and the signer's thumbprint.</summary>
    /// <param name="path">File path, used to read the signer certificate.</param>
    /// <param name="handle">An open handle to the same file that denies writers.</param>
    /// <param name="expectedThumbprint">Pinned SHA-1 thumbprint of the signing certificate.</param>
    /// <returns>Null when the file is validly signed by that certificate; otherwise the problem.</returns>
    public static string? Verify(string path, SafeFileHandle handle, string expectedThumbprint)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedThumbprint);
        using var result = Authenticode.Verify(path, handle, AuthenticodeRevocation.None,
            AuthenticodeSigner.WithThumbprint(expectedThumbprint));
        return result.Outcome switch
        {
            AuthenticodeOutcome.Trusted => null,
            AuthenticodeOutcome.NotTrusted => $"WinVerifyTrust returned 0x{result.TrustStatus:X8}",
            AuthenticodeOutcome.SignerMismatch =>
                $"signed by {result.Signer!.Thumbprint}, expected {expectedThumbprint}",
            _ => $"the signer could not be read: {result.SignerError}"
        };
    }
}
