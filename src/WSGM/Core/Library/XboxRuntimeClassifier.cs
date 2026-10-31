using System;
using System.Linq;

namespace WSGM.Core;

/// <summary>Decides which launch route a packaged title needs, from its own metadata.</summary>
/// <remarks>
///     <para>
///         Xbox is a source of games, not one process model: both UWP and Win32 titles ship as
///         packages. An Xbox install, a WindowsApps path, an <c>.exe</c> extension and the mere
///         existence of package identity all classify nothing, which is why none of them appears
///         here.
///     </para>
///     <para>
///         <see cref="XboxRuntime.Unknown" /> is a first-class outcome, not a defensive default. The
///         two routes differ in what they write into the game, and there is no validated route for a
///         title that is neither shape, so an unclassified title is excluded from import rather than
///         pushed down a route that was never tried on it.
///     </para>
/// </remarks>
public static class XboxRuntimeClassifier
{
    /// <summary>The entry point every full-trust packaged application declares.</summary>
    private const string FullTrustEntryPoint = "Windows.FullTrustApplication";

    /// <summary>The launch helper a GDK title activates.</summary>
    private const string GameLaunchHelper = "gamelaunchhelper.exe";

    /// <summary>The package family every GDK title depends on.</summary>
    private const string GamingServices = "Microsoft.GamingServices";

    /// <summary>Classifies one application.</summary>
    /// <param name="facts">What the package's own metadata says.</param>
    /// <returns>The runtime and the evidence for it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facts" /> is null.</exception>
    public static XboxRuntimeClassification Classify(XboxPackageFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.ManifestReadable)
        {
            return new XboxRuntimeClassification(XboxRuntime.Unknown,
                "This package's manifest could not be read, so nothing about it is established.");
        }

        if (facts.ApplicationId.Length == 0)
        {
            return new XboxRuntimeClassification(XboxRuntime.Unknown,
                "This package declares no application to launch.");
        }

        // More than one application means the AUMID had to be chosen rather than read, and which
        // one is the game is exactly what is not established.
        if (facts.ApplicationCount > 1)
        {
            return new XboxRuntimeClassification(XboxRuntime.Unknown,
                $"This package declares {facts.ApplicationCount} applications, so which one is the "
                + "game is not established.");
        }

        // The full-trust entry point is what makes an application full trust; the capability alone,
        // with no entry point declared, establishes no launch route.
        var fullTrust = facts.EntryPoint.Equals(FullTrustEntryPoint, StringComparison.OrdinalIgnoreCase);
        if (!fullTrust && facts.EntryPoint.Length == 0)
        {
            return new XboxRuntimeClassification(XboxRuntime.Unknown,
                facts.HasRunFullTrust
                    ? "This package declares full trust but no entry point, so its runtime is not established."
                    : "This package declares no entry point, so its runtime is not established.");
        }

        // Contradictory evidence. A WinRT entry point declaring full trust describes neither shape,
        // and picking one of them would be a guess about what the process will actually be.
        if (facts.HasRunFullTrust
            && facts.EntryPoint.Length > 0
            && !facts.EntryPoint.Equals(FullTrustEntryPoint, StringComparison.OrdinalIgnoreCase))
        {
            return new XboxRuntimeClassification(XboxRuntime.Unknown,
                "This package declares full trust alongside a WinRT entry point, which describes "
                + "neither launch route.");
        }

        if (fullTrust)
        {
            if (facts.HasMicrosoftGameConfig)
            {
                return new XboxRuntimeClassification(XboxRuntime.PackagedWin32Gdk,
                    "Full trust, and the package carries MicrosoftGame.config.");
            }

            if (facts.Executable.Equals(GameLaunchHelper, StringComparison.OrdinalIgnoreCase))
            {
                return new XboxRuntimeClassification(XboxRuntime.PackagedWin32Gdk,
                    $"Full trust, and the package activates {GameLaunchHelper}.");
            }

            if (facts.PackageDependencies.Any(dependency =>
                    dependency.StartsWith(GamingServices, StringComparison.OrdinalIgnoreCase)))
            {
                return new XboxRuntimeClassification(XboxRuntime.PackagedWin32Gdk,
                    $"Full trust, and the package depends on {GamingServices}.");
            }

            // A packaged Win32 application that is not a GDK title. The demonstrated route works by
            // setting Steam up in the GDK launch helper, so there is nothing here for it to use.
            return new XboxRuntimeClassification(XboxRuntime.Unknown,
                "Full trust with no GDK evidence, so neither demonstrated launch route applies.");
        }

        // A WinRT entry point beside a game config describes neither shape either.
        if (facts.HasMicrosoftGameConfig)
        {
            return new XboxRuntimeClassification(XboxRuntime.Unknown,
                "A WinRT entry point beside MicrosoftGame.config is contradictory evidence.");
        }

        return new XboxRuntimeClassification(XboxRuntime.NativeUwp,
            "A WinRT entry point with no full-trust capability, so it runs in an AppContainer.");
    }
}
