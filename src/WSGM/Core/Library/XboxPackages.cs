using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;

namespace WSGM.Core;

/// <summary>Lists the packaged applications installed for this user.</summary>
/// <remarks>
///     <para>
///         The only place in the importer that touches WinRT, so everything else is testable
///         against fixtures. One package can declare several launchable entries, and each becomes a
///         candidate with its own AUMID; a game shipping a launcher entry beside its main one is
///         real, and is exactly the ambiguity that makes a package unclassifiable.
///     </para>
///     <para>
///         The AUMID comes from <c>GetAppListEntries</c> rather than being composed from the family
///         name and an application id read out of the manifest. Windows already knows it, and
///         composing it by hand is how a title with an unexpected manifest shape gets a launch
///         command that does not work.
///     </para>
///     <para>
///         Every package is read inside its own try: one mid-update or malformed package must not
///         fail the whole scan.
///     </para>
/// </remarks>
public static class XboxPackages
{
    /// <summary>More packages than this means something is wrong, not that many are installed.</summary>
    private const int MaximumPackages = 1024;

    /// <summary>Lists every candidate this machine has.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>One entry per launchable application, in no particular order.</returns>
    public static IReadOnlyList<InstalledPackage> Enumerate(CancellationToken cancellationToken)
    {
        List<InstalledPackage> found = [];
        IEnumerable<Package> packages;
        try
        {
            PackageManager manager = new();
            using var identity = WindowsIdentity.GetCurrent();
            packages = manager.FindPackagesForUser(identity.User?.Value ?? string.Empty);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"Installed packages could not be listed: {ex.Message}");
            return found;
        }

        var considered = 0;
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++considered > MaximumPackages)
            {
                Log.Warn($"Stopped listing packages after {MaximumPackages}.");
                break;
            }

            try
            {
                Describe(package, found);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                           or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
            {
                // A package being updated, or one whose location cannot be read. Skipped rather
                // than allowed to end the scan.
                Log.Debug($"Skipped a package while listing: {ex.Message}");
            }
        }

        return found;
    }

    /// <summary>Reads a file from a package, or null when it cannot be read.</summary>
    /// <param name="path">The file to read.</param>
    /// <remarks>
    ///     <c>WindowsApps</c> is access-controlled, so an unreadable package file is an ordinary
    ///     outcome that means one piece of evidence is unavailable, not that anything is wrong.
    /// </remarks>
    public static string? ReadPackageFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static void Describe(Package package, List<InstalledPackage> found)
    {
        if (package.IsFramework || package.IsResourcePackage || package.IsBundle || package.IsOptional)
        {
            return;
        }

        // Only what the Store installed. A sideloaded or system-signed package is not something a
        // user bought and expects to find in their library.
        if (package.SignatureKind != PackageSignatureKind.Store)
        {
            return;
        }

        // A package Windows itself reports as not OK is mid-update, modified, disabled or
        // licence-blocked. Offering it would generate a shortcut that cannot launch.
        if (package.Status is { } status && !status.VerifyIsOK())
        {
            return;
        }

        var installPath = string.Empty;
        try
        {
            installPath = package.InstalledLocation?.Path ?? string.Empty;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       or System.Runtime.InteropServices.COMException)
        {
            // Documented to throw for some Store packages. The candidate is still worth listing;
            // it simply has less evidence behind it.
        }

        var family = package.Id?.FamilyName ?? string.Empty;
        if (family.Length == 0)
        {
            return;
        }

        foreach (var entry in package.GetAppListEntries())
        {
            var aumid = entry?.AppUserModelId ?? string.Empty;
            if (aumid.Length == 0)
            {
                continue;
            }

            var separator = aumid.IndexOf('!');
            found.Add(new InstalledPackage(
                aumid,
                family,
                DisplayNameOf(entry),
                installPath,
                separator > 0 && separator < aumid.Length - 1 ? aumid[(separator + 1)..] : string.Empty));
        }
    }

    private static string DisplayNameOf(AppListEntry? entry)
    {
        try
        {
            return entry?.DisplayInfo?.DisplayName ?? string.Empty;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
