using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WSGM.Setup.Engine;

/// <summary>Explicit setup-only installation of the Steam Deck helper's embedded driver.</summary>
internal static class InpOutInstaller
{
    internal static bool Install(string appDirectory)
    {
        var path = Path.Combine(appDirectory, "Resources", "InpOut", "inpoutx64.dll");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexString(SHA256.HashData(held));
        if (digest != "5F27ED4D5CD58A1EE23DEEB802E09E73F3A1D884CE2135F6E827F67B171269E7")
        {
            throw new InvalidDataException("Steam Deck helper does not match the pinned official distribution.");
        }

        // The upstream DLL installs its embedded driver on first load. This belongs exclusively
        // to an explicit setup step; LibHandheld refuses to load it without an existing driver.
        var library = NativeLibrary.Load(path);
        try
        {
            var ready = Marshal.GetDelegateForFunctionPointer<IsDriverOpen>(
                NativeLibrary.GetExport(library, "IsInpOutDriverOpen"));
            if (ready() == 0)
            {
                throw new InvalidOperationException("Windows did not open the Steam Deck firmware driver. "
                                                    + "Setup has not changed Windows driver security settings.");
            }

            return true;
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsDriverOpen();
}
