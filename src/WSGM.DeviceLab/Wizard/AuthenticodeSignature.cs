using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace WSGM.DeviceLab.Wizard;

/// <summary>Checks that a file carries a valid Authenticode signature from one exact certificate.</summary>
/// <remarks>
///     Trust is verified against the caller's open handle, so the result describes the bytes the caller
///     holds. Revocation is not checked: a tester may be offline, and the pinned SHA-256 already binds the
///     installer to the reviewed release.
/// </remarks>
internal static class AuthenticodeSignature
{
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckNone = 0x10;
    private const uint CacheOnlyUrlRetrieval = 0x1000;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <summary>Verifies the signature and the signer's thumbprint.</summary>
    /// <param name="path">File path, used to read the signer certificate.</param>
    /// <param name="handle">An open handle to the same file that denies writers.</param>
    /// <param name="expectedThumbprint">Pinned SHA-1 thumbprint of the signing certificate.</param>
    /// <returns>Null when the file is validly signed by that certificate; otherwise the problem.</returns>
    public static string? Verify(string path, SafeFileHandle handle, string expectedThumbprint)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedThumbprint);
        var trust = VerifyTrust(path, handle);
        if (trust != 0)
        {
            return $"WinVerifyTrust returned 0x{trust:X8}";
        }

        try
        {
            // X509Certificate.CreateFromSignedFile is the only managed way to read an Authenticode
            // signer; X509CertificateLoader does not parse signed PE files.
#pragma warning disable SYSLIB0057
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return string.Equals(signer.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"signed by {signer.Thumbprint}, expected {expectedThumbprint}";
        }
        catch (CryptographicException ex)
        {
            return $"the signer could not be read: {ex.Message}";
        }
    }

    private static int VerifyTrust(string path, SafeFileHandle handle)
    {
        var added = false;
        var fileInfo = IntPtr.Zero;
        try
        {
            handle.DangerousAddRef(ref added);
            var file = new FileInfo
            {
                Size = (uint)Marshal.SizeOf<FileInfo>(),
                FilePath = path,
                File = handle.DangerousGetHandle()
            };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            Marshal.StructureToPtr(file, fileInfo, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                File = fileInfo,
                StateAction = StateActionVerify,
                ProviderFlags = RevocationCheckNone | CacheOnlyUrlRetrieval
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.StateAction = StateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            if (fileInfo != IntPtr.Zero)
            {
                Marshal.DestroyStructure<FileInfo>(fileInfo);
                Marshal.FreeHGlobal(fileInfo);
            }

            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public uint Size;
        public string FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
