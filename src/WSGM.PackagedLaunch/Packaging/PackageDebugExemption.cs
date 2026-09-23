using System;
using System.Runtime.InteropServices;

namespace WSGM.PackagedLaunch;

/// <summary>Takes a packaged title out of Process Lifetime Management for one session.</summary>
/// <remarks>
///     <para>
///         Windows suspends a packaged app when it loses the foreground, which for a game behind
///         Steam's overlay means it freezes. <c>IPackageDebugSettings.EnableDebugging</c> with no
///         debugger command line is the supported way out: a package marked for debugging is exempt,
///         and that is how a debugger keeps one responsive.
///     </para>
///     <para>
///         The exemption outlives this process if it is killed, so every one is journalled before it
///         is requested. See <see cref="PackageDebugRecoveryRecord" />.
///     </para>
/// </remarks>
internal sealed class PackageDebugExemption(PackageDebugRecoveryRecord journal) : IDisposable
{
    private string? _packageFullName;
    private object? _settings;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_settings is not null && _packageFullName is { } packageFullName)
        {
            // Releasing is package-wide, so the journal decides and releases in one step: another
            // imported title from this package still running keeps the exemption, and the last
            // launcher out puts the package back.
            var settings = (NativeMethods.IPackageDebugSettings)_settings;
            switch (journal.Retire(packageFullName, Environment.ProcessId, name => Disable(settings, name)))
            {
                case PackageRetirement.KeptForAnotherLauncher:
                    PackagedLaunchLog.Info(
                        $"Package lifetime: {packageFullName} stays exempt for another running launcher.");
                    break;
                case PackageRetirement.Failed:
                    PackagedLaunchLog.Warn(
                        $"Package lifetime: {packageFullName} is still exempt; a later sweep releases it.");
                    break;
            }
        }

        Release();
    }

    /// <summary>Exempts one package, recording it first so a kill cannot lose it.</summary>
    /// <param name="packageFullName">The package to exempt.</param>
    /// <param name="launcherProcessId">This launcher's process id.</param>
    /// <param name="launcherStartedUtc">When this launcher started.</param>
    /// <returns>Whether the package is now exempt.</returns>
    internal bool Request(string packageFullName, int launcherProcessId, DateTime? launcherStartedUtc)
    {
        if (_packageFullName is not null)
        {
            return true;
        }

        // Journal first, and refuse if that did not stick. A record for an exemption that was never
        // granted costs one harmless DisableDebugging on the next sweep; an exemption with no record
        // is a package Windows never suspends again for the rest of this machine's life, because
        // only a record can tell a later sweep to put it back.
        if (!journal.Add(packageFullName, launcherProcessId, launcherStartedUtc))
        {
            PackagedLaunchLog.Warn(
                $"Not exempting {packageFullName} from package lifetime: the recovery journal could "
                + "not record it, so a killed launcher could never put it back. The game may be "
                + "suspended when it loses the foreground.");
            return false;
        }

        try
        {
            _settings = new NativeMethods.PackageDebugSettings();
            var result = ((NativeMethods.IPackageDebugSettings)_settings)
                .EnableDebugging(packageFullName, null, IntPtr.Zero);
            if (result < 0)
            {
                PackagedLaunchLog.Warn(
                    $"Package lifetime exemption refused for {packageFullName}: 0x{result:X8}. "
                    + "The game may be suspended when it loses the foreground.");
                journal.Remove(packageFullName, launcherProcessId);
                Release();
                return false;
            }

            _packageFullName = packageFullName;
            PackagedLaunchLog.Info($"Package lifetime: {packageFullName} is exempt for this session.");
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            PackagedLaunchLog.Warn($"Package lifetime exemption unavailable: {ex.Message}");
            journal.Remove(packageFullName, launcherProcessId);
            Release();
            return false;
        }
    }

    /// <summary>Releases every exemption whose owning launcher is gone.</summary>
    /// <param name="journal">The journal to sweep.</param>
    /// <returns>How many packages were released.</returns>
    /// <remarks>
    ///     Idempotent, and safe to run when nothing is stale: a package that is not exempt answers
    ///     <c>DisableDebugging</c> without complaint.
    /// </remarks>
    internal static int ReleaseAbandoned(PackageDebugRecoveryRecord journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        object? settings = null;
        try
        {
            return journal.ReleaseAbandoned(packageFullName =>
            {
                NativeMethods.IPackageDebugSettings api;
                try
                {
                    settings ??= new NativeMethods.PackageDebugSettings();
                    api = (NativeMethods.IPackageDebugSettings)settings;
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
                {
                    PackagedLaunchLog.Warn($"Package lifetime recovery unavailable: {ex.Message}");
                    return false;
                }

                if (!Disable(api, packageFullName))
                {
                    return false;
                }

                PackagedLaunchLog.Info($"Package lifetime: released a stale exemption for {packageFullName}.");
                return true;
            });
        }
        finally
        {
            FinalRelease(settings);
        }
    }

    /// <summary>Puts one package back under lifetime management.</summary>
    /// <param name="settings">The debug settings object.</param>
    /// <param name="packageFullName">The package.</param>
    /// <returns>Whether Windows accepted the release.</returns>
    private static bool Disable(NativeMethods.IPackageDebugSettings settings, string packageFullName)
    {
        try
        {
            var result = settings.DisableDebugging(packageFullName);
            if (result >= 0)
            {
                return true;
            }

            PackagedLaunchLog.Warn(
                $"Could not release the package lifetime exemption for {packageFullName}: 0x{result:X8}");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            PackagedLaunchLog.Warn(
                $"Could not release the package lifetime exemption for {packageFullName}: {ex.Message}");
        }

        return false;
    }

    private static void FinalRelease(object? settings)
    {
        if (settings is not null && Marshal.IsComObject(settings))
        {
            Marshal.FinalReleaseComObject(settings);
        }
    }

    private void Release()
    {
        FinalRelease(_settings);
        _settings = null;
        _packageFullName = null;
    }
}
