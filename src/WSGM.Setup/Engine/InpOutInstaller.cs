using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace WSGM.Setup.Engine;

/// <summary>Explicit setup-only installation of the Steam Deck helper's embedded driver.</summary>
internal static class InpOutInstaller
{
    internal static bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\inpoutx64");
        return key is not null;
    }

    internal static bool Uninstall(IRuntimeShutdown runtime)
    {
        if (!IsInstalled())
        {
            return true;
        }

        runtime.Run(WindowsSetup.SystemTool("sc.exe"), "stop inpoutx64");
        return runtime.Run(WindowsSetup.SystemTool("sc.exe"), "delete inpoutx64") == 0;
    }

    internal static bool Install(string appDirectory)
    {
        var path = Path.Combine(appDirectory, "Resources", "InpOut", "inpoutx64.dll");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexString(SHA256.HashData(held));
        using var resource = typeof(InpOutInstaller).Assembly.GetManifestResourceStream("WSGM.InpOut.Pin")
                             ?? throw new InvalidOperationException("Steam Deck helper pin is missing.");
        using var pin = JsonDocument.Parse(resource);
        var expected = pin.RootElement.GetProperty("component").GetProperty("sha256").GetString();
        if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
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
