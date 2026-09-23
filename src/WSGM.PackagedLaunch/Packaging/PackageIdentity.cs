using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.PackagedLaunch;

/// <summary>Resolves package identity and decides what an activated game actually is.</summary>
/// <remarks>
///     Classification happens here, from the live process, rather than from the shortcut. A package
///     can be updated after its shortcut was written, and the difference between the two routes is
///     whether anything is written into the game, so the decision is made from what is running.
/// </remarks>
internal static class PackageIdentity
{
    /// <summary>The GDK launch helper every packaged Win32 title of that shape activates.</summary>
    private const string GameLaunchHelper = "gamelaunchhelper.exe";

    /// <summary>The file a GDK title carries in its package root.</summary>
    private const string MicrosoftGameConfig = "MicrosoftGame.config";

    /// <summary>The installed package full name for a family, before anything of it is running.</summary>
    /// <param name="familyName">The package family name.</param>
    /// <returns>The full name, or null when the family is not installed.</returns>
    internal static string? ResolveFullName(string familyName)
    {
        uint count = 0;
        uint bufferLength = 0;
        var status = NativeMethods.FindPackagesByPackageFamily(
            familyName,
            NativeMethods.PackageFilterHead | NativeMethods.PackageFilterDirect,
            ref count,
            IntPtr.Zero,
            ref bufferLength,
            IntPtr.Zero,
            IntPtr.Zero);
        if (status != NativeMethods.ErrorInsufficientBuffer || count == 0)
        {
            return null;
        }

        var names = Marshal.AllocHGlobal((int)count * IntPtr.Size);
        var buffer = Marshal.AllocHGlobal((int)bufferLength * sizeof(char));
        try
        {
            status = NativeMethods.FindPackagesByPackageFamily(
                familyName,
                NativeMethods.PackageFilterHead | NativeMethods.PackageFilterDirect,
                ref count,
                names,
                ref bufferLength,
                buffer,
                IntPtr.Zero);
            return status == NativeMethods.ErrorSuccess
                ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(names))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The package full name a running process carries, or null when it carries none.</summary>
    /// <param name="processId">The process to read.</param>
    internal static string? FullNameOf(int processId)
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            uint length = 0;
            if (NativeMethods.GetPackageFullName(process, ref length, null) != NativeMethods.ErrorInsufficientBuffer)
            {
                return null;
            }

            StringBuilder buffer = new((int)length);
            return NativeMethods.GetPackageFullName(process, ref length, buffer) == NativeMethods.ErrorSuccess
                ? buffer.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>Where an installed package's files live, or null when that cannot be read.</summary>
    /// <param name="packageFullName">The package full name.</param>
    internal static string? InstallPathOf(string packageFullName)
    {
        uint length = 0;
        if (NativeMethods.GetPackagePathByFullName(packageFullName, ref length, null)
            != NativeMethods.ErrorInsufficientBuffer)
        {
            return null;
        }

        StringBuilder buffer = new((int)length);
        return NativeMethods.GetPackagePathByFullName(packageFullName, ref length, buffer)
               == NativeMethods.ErrorSuccess
            ? buffer.ToString()
            : null;
    }

    /// <summary>Decides what the activated process is, and says what decided it.</summary>
    /// <param name="seed">The process activation returned.</param>
    /// <param name="installPath">Its package's install path, or null when unknown.</param>
    /// <returns>The runtime and the evidence for it, both fit for the log.</returns>
    /// <remarks>
    ///     <para>
    ///         An AppContainer token is decisive on its own: that is what makes the Moonlighter route
    ///         necessary, because inside one, Steam's IPC objects resolve to a private namespace.
    ///     </para>
    ///     <para>
    ///         Full trust alone is not enough for the other route. The PowerWash route works by
    ///         setting Steam up in the GDK launch helper and letting Steam follow the handoff, so a
    ///         full-trust packaged title with no GDK evidence has no demonstrated route and stays
    ///         unknown rather than being pushed down one that was never tried on its shape.
    ///     </para>
    /// </remarks>
    internal static (PackagedRuntime Runtime, string Evidence) Classify(ProcessFacts seed, string? installPath)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.IsAppContainer is null)
        {
            return (PackagedRuntime.Unknown,
                $"The token of {seed.Name} could not be read, so its runtime is unestablished.");
        }

        if (seed.IsAppContainer == true)
        {
            return (PackagedRuntime.AppContainer,
                $"{seed.Name} runs in an AppContainer at {Integrity(seed)} integrity.");
        }

        if (seed.Name.Equals(GameLaunchHelper, StringComparison.OrdinalIgnoreCase))
        {
            return (PackagedRuntime.PackagedWin32,
                $"Activation returned {seed.Name}, the GDK launch helper.");
        }

        if (installPath is { Length: > 0 } && HasGameConfig(installPath))
        {
            return (PackagedRuntime.PackagedWin32,
                $"{seed.Name} is full trust and its package carries {MicrosoftGameConfig}.");
        }

        return (PackagedRuntime.Unknown,
            $"{seed.Name} is full trust but its package carries no {MicrosoftGameConfig} and activation "
            + "did not return a GDK launch helper.");
    }

    private static string Integrity(ProcessFacts seed)
    {
        return seed.Integrity.Length > 0 ? seed.Integrity : "unreadable";
    }

    private static bool HasGameConfig(string installPath)
    {
        try
        {
            return File.Exists(Path.Combine(installPath, MicrosoftGameConfig));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // WindowsApps is ACL'd, so an unreadable package root is an ordinary outcome here and
            // means only that this particular piece of evidence is unavailable.
            return false;
        }
    }
}
