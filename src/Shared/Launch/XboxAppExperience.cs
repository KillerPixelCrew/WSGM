// Shared between WSGM and WSGM.PackagedLaunch (linked as a source file). No Log, no ConfigStore,
// explicit usings.

using System;

namespace WSGM.Core;

/// <summary>Which experience the Xbox app's entry point opens.</summary>
/// <remarks>
///     <para>
///         The Xbox package's visible entry, <c>XboxPcAppCE.exe</c>, is a Win32 WinUI and WebView2 app. It
///         either opens its own dashboard window or hands off to the older UWP app,
///         <c>Microsoft.Xbox.AppL</c>. That UWP app needs Explorer's immersive shell to show its view, so in
///         Game Mode its activation times out after 45 seconds and the window goes black.
///     </para>
///     <para>
///         From package 2612.1001.23.0 the entry point reads the per-user DWORD below at startup: 1 keeps it
///         in its own dashboard, any other value hands off, and a missing value falls back to Microsoft's
///         default. Earlier packages always hand off, whatever the value says.
///     </para>
/// </remarks>
internal static class XboxAppExperience
{
    /// <summary>The Xbox app's Start entry.</summary>
    internal const string AppUserModelId = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    /// <summary>The app's own per-user settings key under HKEY_CURRENT_USER.</summary>
    internal const string SettingsKey = @"Software\Microsoft\Xbox\GamingApp";

    /// <summary>The DWORD value that selects the experience.</summary>
    internal const string SelectorValue = "XboxPcAppCE";

    /// <summary>The value that keeps the entry point in its own dashboard.</summary>
    internal const int NewExperience = 1;

    /// <summary>Whether activating this application opens the Xbox app.</summary>
    /// <param name="appUserModelId">The application being activated.</param>
    /// <returns>True for the Xbox app's entry point.</returns>
    internal static bool Applies(string appUserModelId)
    {
        return string.Equals(appUserModelId.Trim(), AppUserModelId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the stored value still has to be set before activation.</summary>
    /// <param name="stored">The value read from the registry, or null when absent.</param>
    /// <returns>True unless the new experience is already selected.</returns>
    internal static bool NeedsSelection(object? stored)
    {
        return stored is not int value || value != NewExperience;
    }
}
