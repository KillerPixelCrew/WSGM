// SPDX-License-Identifier: MIT

namespace WSGM.Shared;

/// <summary>Cross-version kernel object names and one-shot exit codes shared by WSGM, setup and Device Lab.</summary>
internal static class SessionProtocolNames
{
    /// <summary>
    ///     What <c>--restore-steam-content</c> answers when it restored Steam's files but Steam's own
    ///     Startup Movie choice is still set aside, so setup tells the user to choose it again.
    /// </summary>
    internal const int SteamStartupMovieStillSetAside = 2;

    internal const string ExitForUpdate = @"Local\WSGM.ExitForUpdate";
    internal const string ExitForUninstall = @"Local\WSGM.ExitForUninstall";
    internal const string ShellMutex = @"Local\WSGM.Shell";
    internal const string DeviceOwner = @"Global\WSGM.DeviceOwner";
    internal const string AnchorRecoverySettled = @"Local\WSGM.ShellAnchor.RecoverySettled";
}
