using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Native Authenticode chain and revocation verification.</summary>
internal static partial class NativeAuthenticode
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x00000080;
    private const uint WtdLifetimeSigningFlag = 0x00000800;
    private static readonly Guid GenericVerifyV2 = new(
        0x00AAC56B,
        0xCD44,
        0x11D0,
        0x8C,
        0xC2,
        0x00,
        0xC0,
        0x4F,
        0xC2,
        0x95,
        0xEE);

    /// <summary>Verifies the embedded signature, chain, timestamp, and revocation state.</summary>
    /// <param name="path">Exact file to verify.</param>
    /// <returns>Zero on success; otherwise the WinTrust status code.</returns>
    internal static unsafe int VerifyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        fixed (char* pathPointer = path)
        {
            WinTrustFileInfo file = new()
            {
                StructSize = (uint)sizeof(WinTrustFileInfo),
                FilePath = pathPointer,
            };
            WinTrustData data = new()
            {
                StructSize = (uint)sizeof(WinTrustData),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                File = &file,
                StateAction = WtdStateActionIgnore,
                ProviderFlags = WtdRevocationCheckChainExcludeRoot | WtdLifetimeSigningFlag,
            };
            Guid action = GenericVerifyV2;
            return WinVerifyTrust(0, ref action, ref data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WinTrustFileInfo
    {
        public uint StructSize;
        public char* FilePath;
        public nint FileHandle;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WinTrustData
    {
        public uint StructSize;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public WinTrustFileInfo* File;
        public uint StateAction;
        public nint StateData;
        public char* UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust", SetLastError = true)]
    private static partial int WinVerifyTrust(
        nint window,
        ref Guid actionId,
        ref WinTrustData trustData);
}
